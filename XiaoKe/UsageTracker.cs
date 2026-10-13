using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace XiaoKe;

// 讀取 Claude Code 本地對話記錄（~/.claude/projects/**/*.jsonl），按 API 價格估算花費。
// - 只掃最近 KeepDays 天改動過的檔案；按位元組偏移量增量讀取，結果快取到磁碟（usage-cache.json）。
// - 同一條回覆會分多行寫入（message.id + requestId 相同），以最後一行的 usage 為準。
// - 主對話裡出現 stop_reason=end_turn 時視為「一輪結束」，把這一輪（含子代理）的花費發出去。

sealed class Price
{
    public double Input, Output, CacheWrite5m, CacheWrite1h, CacheRead;
}

sealed class Pricing
{
    public readonly Dictionary<string, Price> Models = new();
    public readonly List<KeyValuePair<string, string>> Family = new(); // 依檔案順序比對

    public static Pricing Load(string file)
    {
        var p = new Pricing();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
                foreach (var m in models.EnumerateObject())
                    p.Models[m.Name] = new Price
                    {
                        Input = Num(m.Value, "input"), Output = Num(m.Value, "output"),
                        CacheWrite5m = Num(m.Value, "cacheWrite5m"), CacheWrite1h = Num(m.Value, "cacheWrite1h"),
                        CacheRead = Num(m.Value, "cacheRead"),
                    };
            if (root.TryGetProperty("_family", out var fam) && fam.ValueKind == JsonValueKind.Object)
                foreach (var f in fam.EnumerateObject())
                    if (f.Value.ValueKind == JsonValueKind.String) p.Family.Add(new(f.Name, f.Value.GetString()!));
        }
        catch { p.Models.Clear(); p.Family.Clear(); }
        return p;
    }

    static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>先完全比對模型名，找不到再依名稱含 fable/opus/sonnet/haiku 走 _family。</summary>
    public Price? PriceFor(string? model)
    {
        if (model != null && Models.TryGetValue(model, out var p)) return p;
        foreach (var f in Family)
            if (model != null && model.Contains(f.Key, StringComparison.Ordinal))
            {
                Models.TryGetValue(f.Value, out var fp);
                return fp;
            }
        return null;
    }
}

sealed class FileState
{
    [JsonPropertyName("offset")] public long Offset { get; set; }
}

sealed class Entry
{
    [JsonPropertyName("d")] public string D { get; set; } = "";
    [JsonPropertyName("t")] public long T { get; set; }
    [JsonPropertyName("c")] public double C { get; set; }
    [JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? M { get; set; }
    [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? S { get; set; }
}

sealed class CacheFile
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, FileState> Files { get; set; } = new();
    [JsonPropertyName("entries")] public Dictionary<string, Entry> Entries { get; set; } = new();
}

sealed record TurnInfo(string SessionId, double Cost, string? Model, string? Cwd);

sealed record UsageStats(
    double Today, int TodayMsgs, double Week,
    List<KeyValuePair<string, double>> Days,
    List<KeyValuePair<string, double>> ByModel)
{
    // 以下由 PetApp.WithQuota 補上
    public OfficialQuota? Rl { get; init; }
    public string? RlErr { get; init; }
    public bool RlTried { get; init; } // 這次執行期間是否已經讀過至少一次官方額度（不論成敗）
}

sealed class UsageTracker
{
    const int KeepDays = 8;

    readonly string root;
    readonly string pricingFile;
    readonly string cacheFile;
    readonly object gate = new();

    Pricing pricing = new();
    Dictionary<string, FileState> files = new();
    Dictionary<string, Entry> entries = new();
    readonly Dictionary<string, double> turnAcc = new(); // sessionId -> 本輪累計
    readonly HashSet<string> ended = new();
    volatile bool live; // 首次掃描期間不發「一輪結束」
    int busy;
    long lastSave;
    CancellationTokenSource? cts;

    public event Action<TurnInfo>? Turn;
    public event Action<UsageStats>? StatsChanged;

    public UsageTracker(string pricingFile, string cacheFile, string? root = null)
    {
        this.root = root ?? Paths.ClaudeProjects;
        this.pricingFile = pricingFile;
        this.cacheFile = cacheFile;
        LoadPricing();
        LoadCache();
    }

    public void LoadPricing()
    {
        var p = Pricing.Load(pricingFile);
        lock (gate) pricing = p;
    }

    /// <summary>重新載入價格並整個重掃（價格變了，舊的花費要重算）。</summary>
    public void Reload()
    {
        LoadPricing();
        lock (gate) { entries = new(); files = new(); live = false; }
        ThreadPool.QueueUserWorkItem(_ => Scan());
    }

    // ---------- 花費 ----------
    static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static bool Truthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => e.GetDouble() != 0,
        JsonValueKind.String => e.GetString()!.Length > 0,
        JsonValueKind.Object or JsonValueKind.Array => true,
        _ => false,
    };

    double Cost(string? model, JsonElement u)
    {
        var p = pricing.PriceFor(model);
        if (p == null || u.ValueKind != JsonValueKind.Object) return 0;
        double w1h = 0, w5m = 0;
        if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
        {
            w1h = Num(cc, "ephemeral_1h_input_tokens");
            w5m = Num(cc, "ephemeral_5m_input_tokens");
        }
        if (w1h == 0 && w5m == 0) w5m = Num(u, "cache_creation_input_tokens");
        double usd = (Num(u, "input_tokens") * p.Input + Num(u, "output_tokens") * p.Output +
                      w5m * p.CacheWrite5m + w1h * p.CacheWrite1h +
                      Num(u, "cache_read_input_tokens") * p.CacheRead) / 1e6;
        return Str(u, "speed") == "fast" ? usd * 2 : usd;
    }

    public static string LocalDate(long ms)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        return $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
    }

    // ---------- 快取 ----------
    void LoadCache()
    {
        try
        {
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cacheFile));
            if (c is { V: 3 }) { files = c.Files ?? new(); entries = c.Entries ?? new(); }
        }
        catch { /* 沒有快取就全量掃 */ }
    }

    public void SaveCache()
    {
        try
        {
            byte[] bytes;
            lock (gate) bytes = JsonSerializer.SerializeToUtf8Bytes(new CacheFile { V = 3, Files = files, Entries = entries });
            var dir = Path.GetDirectoryName(cacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = cacheFile + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, cacheFile, true);
        }
        catch { /* ignore */ }
    }

    void Prune()
    {
        var cutoff = LocalDate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - KeepDays * 86400_000L);
        var dead = new List<string>();
        foreach (var kv in entries) if (string.CompareOrdinal(kv.Value.D, cutoff) < 0) dead.Add(kv.Key);
        foreach (var k in dead) entries.Remove(k);
    }

    // ---------- 掃描 ----------
    List<(string Path, long Size)> ListFiles()
    {
        var outList = new List<(string, long)>();
        var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
        void Walk(string dir, int depth)
        {
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos(); } catch { return; }
            try
            {
                foreach (var it in items)
                {
                    if ((it.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < 6) Walk(it.FullName, depth + 1);
                    }
                    else if (it.Name.EndsWith(".jsonl", StringComparison.Ordinal) && it is FileInfo fi)
                    {
                        try { if (fi.LastWriteTimeUtc >= cutoff) outList.Add((fi.FullName, fi.Length)); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
        }
        Walk(root, 0);
        return outList;
    }

    static readonly byte[] KAssistant = "\"assistant\""u8.ToArray();
    static readonly byte[] KUsage = "\"usage\""u8.ToArray();

    void HandleLine(byte[] buf, int start, int len, string file)
    {
        var span = new ReadOnlySpan<byte>(buf, start, len);
        if (span.IndexOf(KAssistant) < 0 || span.IndexOf(KUsage) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf, start, len));
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || Str(o, "type") != "assistant") return;
            if (!o.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
            if (!m.TryGetProperty("usage", out var u) || !Truthy(u)) return;
            var model = Str(m, "model");
            if (model == "<synthetic>") return;
            var key = (Str(m, "id") ?? Str(o, "uuid") ?? "undefined") + ":" + (Str(o, "requestId") ?? "");
            long t = 0;
            if (Str(o, "timestamp") is { } ts &&
                DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                t = dto.ToUnixTimeMilliseconds();
            if (t == 0) t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sessionId = Str(o, "sessionId");
            TurnInfo? turn = null;

            lock (gate)
            {
                double c = Cost(model, u);
                entries.TryGetValue(key, out var prev);
                double delta = c - (prev?.C ?? 0);
                entries[key] = new Entry { D = LocalDate(t), T = t, C = c, M = model, S = sessionId };

                if (!live) return;
                // 開啟/續接對話時 Claude Code 會把舊紀錄複製進新的記錄檔：時間戳太舊的不算「剛結束的一輪」
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t > 5 * 60_000L) return;
                var sid = sessionId ?? file;
                if (delta != 0) turnAcc[sid] = (turnAcc.TryGetValue(sid, out var a) ? a : 0) + delta;
                bool side = o.TryGetProperty("isSidechain", out var sc) && Truthy(sc);
                if (Str(m, "stop_reason") == "end_turn" && !side && ended.Add(key))
                {
                    turnAcc.TryGetValue(sid, out var acc);
                    turnAcc[sid] = 0;
                    turn = new TurnInfo(sid, acc, model, Str(o, "cwd"));
                }
            }
            if (turn != null) Turn?.Invoke(turn);
        }
        catch { /* 壞行跳過 */ }
    }

    bool ReadFile(string p, long size)
    {
        FileState st;
        lock (gate) st = files.TryGetValue(p, out var s) ? s : new FileState();
        if (size < st.Offset) st.Offset = 0; // 檔案被截斷/重寫
        if (size == st.Offset) { lock (gate) files[p] = st; return false; }

        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var buf = new byte[256 * 1024];
        int have = 0;
        long pos = st.Offset; // buf[0] 對應的檔案位置
        fs.Position = pos;
        while (pos + have < size)
        {
            if (have == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            int want = (int)Math.Min(buf.Length - have, size - (pos + have));
            int n = fs.Read(buf, have, want);
            if (n <= 0) break;
            int from = have;
            have += n;
            int start = 0;
            while (true)
            {
                int nl = Array.IndexOf(buf, (byte)'\n', Math.Max(start, from), have - Math.Max(start, from));
                if (nl < 0) break;
                HandleLine(buf, start, nl - start, p);
                start = nl + 1;
            }
            if (start > 0)
            {
                Buffer.BlockCopy(buf, start, buf, 0, have - start);
                have -= start;
                pos += start;
            }
        }
        // 最後不完整的一行留到下次再讀
        st.Offset = pos;
        lock (gate) files[p] = st;
        return true;
    }

    public void Scan()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            var list = ListFiles();
            bool changed = false;
            foreach (var f in list)
            {
                try { if (ReadFile(f.Path, f.Size)) changed = true; } catch { /* 檔案正被寫，下次再讀 */ }
            }
            var alive = new HashSet<string>();
            foreach (var f in list) alive.Add(f.Path);
            UsageStats? st = null;
            lock (gate)
            {
                var gone = new List<string>();
                foreach (var k in files.Keys) if (!alive.Contains(k)) gone.Add(k);
                foreach (var k in gone) files.Remove(k);
                if (changed || !live) { Prune(); st = ComputeStats(); }
            }
            if (st != null) StatsChanged?.Invoke(st);
            if (changed && Environment.TickCount64 - lastSave > 30_000) { SaveCache(); lastSave = Environment.TickCount64; }
            live = true;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    // ---------- 統計 ----------
    public UsageStats GetStats() { lock (gate) return ComputeStats(); }

    UsageStats ComputeStats()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var today = LocalDate(nowMs);
        var days = new List<KeyValuePair<string, double>>();
        var idx = new Dictionary<string, int>();
        for (int i = 6; i >= 0; i--)
        {
            var d = LocalDate(nowMs - i * 86400_000L);
            if (idx.ContainsKey(d)) continue;
            idx[d] = days.Count;
            days.Add(new(d, 0));
        }
        var byModel = new List<KeyValuePair<string, double>>();
        var mIdx = new Dictionary<string, int>();
        double todayCost = 0;
        int todayMsgs = 0;
        foreach (var e in entries.Values)
        {
            if (idx.TryGetValue(e.D, out var di)) days[di] = new(days[di].Key, days[di].Value + e.C);
            if (e.D == today)
            {
                todayCost += e.C;
                todayMsgs++;
                var mk = e.M ?? "undefined";
                if (mIdx.TryGetValue(mk, out var mi)) byModel[mi] = new(mk, byModel[mi].Value + e.C);
                else { mIdx[mk] = byModel.Count; byModel.Add(new(mk, e.C)); }
            }
        }
        double week = 0;
        foreach (var d in days) week += d.Value;
        return new UsageStats(todayCost, todayMsgs, week, days, byModel);
    }

    // ---------- 生命週期 ----------
    FileSystemWatcher? watcher;

    /// <summary>
    /// 背景掃描。記錄檔一有寫入就觸發（約 1 秒內，合併連續寫入），intervalMs 只是備援輪詢。
    /// </summary>
    public void Start(int intervalMs)
    {
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var wake = new AutoResetEvent(false);
        try
        {
            if (Directory.Exists(root))
            {
                watcher = new FileSystemWatcher(root, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += (_, _) => wake.Set();
                watcher.Created += (_, _) => wake.Set();
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception e) { Log.Error("watcher", e); }

        var th = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try { Scan(); } catch (Exception e) { Log.Error("scan", e); }
                try
                {
                    int i = WaitHandle.WaitAny(new[] { token.WaitHandle, wake }, intervalMs);
                    if (i == 0) break;
                    if (i == 1 && token.WaitHandle.WaitOne(1000)) break; // 合併連續寫入
                }
                catch { break; }
            }
        })
        { IsBackground = true, Name = "usage-scan", Priority = ThreadPriority.BelowNormal };
        th.Start();
    }

    public void Stop()
    {
        try { watcher?.Dispose(); } catch { /* ignore */ }
        try { cts?.Cancel(); } catch { /* ignore */ }
        SaveCache();
    }
}
