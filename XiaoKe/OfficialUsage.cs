using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace XiaoKe;

/// <summary>
/// 官方額度：開一個隱藏的互動式 claude（Windows ConPTY，停用 hook），送 /usage，讀畫面上的
/// 「Current session」與「Current week (all models)」百分比與重置時間，然後關掉。
/// 只開畫面、不送對話，所以不花 token。
/// 啟動時、每輪結束後（至少隔 5 分鐘）、點量槽時各讀一次。
/// </summary>
sealed class ProbeOfficialUsage : IOfficialUsage
{
    static readonly TimeSpan Gap = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ForceGap = TimeSpan.FromSeconds(20);
    static readonly string RealFile = Path.Combine(Paths.Data, "real-usage.json");

    readonly Dispatcher ui;
    readonly Func<bool> enabled;
    readonly Action<bool> onDone; // 讀完（成功與否）通知主程式更新畫面；參數 = 是否成功

    OfficialQuota? real;
    bool probing;
    DateTime lastProbe = DateTime.MinValue;
    DispatcherTimer? retryTimer;

    public string? LastError { get; private set; }
    public bool Tried { get; private set; }

    public ProbeOfficialUsage(Dispatcher ui, Func<bool> enabled, Action<bool> onDone)
    {
        this.ui = ui;
        this.enabled = enabled;
        this.onDone = onDone;
        LoadSaved();
    }

    /// <summary>目前還有效（重置時間還沒過）的官方數字；沒有就回傳 null，量槽顯示「抓不到」。</summary>
    public OfficialQuota? Current()
    {
        if (!enabled() || real == null) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        QuotaWindow? Pick(QuotaWindow? w) => w != null && w.ResetsAt > now ? w : null;
        var week = Pick(real.Week);
        var five = Pick(real.Five);
        return week != null || five != null ? new OfficialQuota(week, five) : null;
    }

    // ---------- 排程 ----------
    public void Probe(bool force)
    {
        if (!enabled() || probing) return;
        var since = DateTime.UtcNow - lastProbe;
        if (force ? since < ForceGap : since < Gap)
        {
            // 太頻繁就排一次延後的重讀，讓最後的數字一定會更新
            if (!force && retryTimer == null)
            {
                retryTimer = new DispatcherTimer { Interval = Gap - since + TimeSpan.FromMilliseconds(500) };
                retryTimer.Tick += (_, _) => { retryTimer!.Stop(); retryTimer = null; Probe(false); };
                retryTimer.Start();
            }
            return;
        }
        probing = true;
        lastProbe = DateTime.UtcNow;
        var cwd = ProbeCwd();
        var th = new Thread(() =>
        {
            ProbeResult r;
            try { r = ProbeRunner.Run(cwd); }
            catch (Exception e) { Log.Error("probe", e); r = ProbeResult.Fail("讀取失敗：" + e.Message); }
            ui.BeginInvoke(() => Finish(r));
        })
        { IsBackground = true, Name = "usage-probe" };
        th.Start();
    }

    void Finish(ProbeResult r)
    {
        probing = false;
        Tried = true;
        if (r.Ok)
        {
            real = new OfficialQuota(r.Week, r.Session);
            LastError = null;
            Save();
        }
        else { LastError = r.Error; Log.Info("官方額度讀取失敗：" + r.Error); }
        onDone(r.Ok);
    }

    /// <summary>claude 會問「信任這個資料夾嗎」，所以用使用者開過 claude 的資料夾：優先找專案根目錄（含 hook.js 的那層）。</summary>
    static string ProbeCwd()
    {
        if (Environment.GetEnvironmentVariable("XIAOKE_PROBE_CWD") is { Length: > 0 } env && Directory.Exists(env)) return env;
        var dir = new DirectoryInfo(Paths.AppDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "hook.js"))) return dir.FullName;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ---------- 存檔（real-usage.json） ----------
    void LoadSaved()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RealFile));
            var o = doc.RootElement;
            QuotaWindow? W(string name) =>
                o.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("pct", out var p) && w.TryGetProperty("resetsAt", out var r)
                    ? new QuotaWindow(p.GetDouble(), (long)r.GetDouble()) : null;
            real = new OfficialQuota(W("week"), W("session"));
        }
        catch { /* 還沒讀過 */ }
    }

    void Save()
    {
        try
        {
            object? W(QuotaWindow? w) => w == null ? null : new { pct = w.Used, resetsAt = w.ResetsAt };
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(RealFile, JsonSerializer.Serialize(new { ok = true, session = W(real?.Five), week = W(real?.Week) }));
        }
        catch { /* ignore */ }
    }
}

sealed record ProbeResult(bool Ok, string? Error, QuotaWindow? Session, QuotaWindow? Week)
{
    public static ProbeResult Fail(string error) => new(false, error, null, null);
}

/// <summary>在 ConPTY 裡跑一次 claude，讀 /usage 畫面。</summary>
static class ProbeRunner
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
    const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly Regex AnsiCsi = new(@"\x1b\[[0-9;?<=>]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    static readonly Regex AnsiOsc = new(@"\x1b\][^\x07]*(\x07|\x1b\\)", RegexOptions.CultureInvariant);
    static readonly Regex Trust = new(@"trust\s*this\s*folder|Do\s*you\s*trust|trust\s*the\s*files", Ci);
    static readonly Regex Login = new(@"OAuth\s*token\s*has\s*expired|Please\s*run\s*/login|Not\s*logged\s*in|Invalid\s*API\s*key", Ci);
    static readonly Regex RateLimited = new(@"endpoint\s*is\s*rate\s*limited", Ci);
    static readonly Regex Prompt = new(@"❯|\?\s*for\s*shortcuts|for\s*shortcuts|shift\+tab\s*to\s*cycle|>\s*Try\s*""", Ci);
    const string SessionLabel = @"Current\s*session";
    const string WeekLabel = @"Current\s*week\s*\(all\s*models\)";

    // 去掉終端機控制碼（游標移動會把空格吃掉，所以後面的比對都要容忍沒有空格）
    public static string Strip(string s) => AnsiOsc.Replace(AnsiCsi.Replace(s, ""), "");

    /// <summary>「6:10pm (Asia/Taipei)」「Oct 13, 2pm (Asia/Taipei)」→ 毫秒時間戳（以本機時區計）。</summary>
    public static long? ParseReset(string text, DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        var t = Regex.Replace(text, @"\(.*?\)", "").Trim();
        var m = Regex.Match(t, @"^(?:([A-Za-z]{3})[a-z]*\.?\s*(\d{1,2}),?\s*)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)", Ci);
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[3].Value) % 12;
        if (m.Groups[5].Value.Equals("pm", StringComparison.OrdinalIgnoreCase)) h += 12;
        int min = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
        DateTime d;
        try
        {
            if (m.Groups[1].Success)
            {
                int mon = Array.IndexOf(Months, m.Groups[1].Value.ToLowerInvariant());
                if (mon < 0) return null;
                int day = int.Parse(m.Groups[2].Value);
                d = new DateTime(now.Year, mon + 1, day, h, min, 0, DateTimeKind.Local);
                if (d < now.AddDays(-1)) d = new DateTime(now.Year + 1, mon + 1, day, h, min, 0, DateTimeKind.Local);
            }
            else
            {
                d = new DateTime(now.Year, now.Month, now.Day, h, min, 0, DateTimeKind.Local);
                if (d <= now) d = d.AddDays(1);
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        return new DateTimeOffset(d).ToUnixTimeMilliseconds();
    }

    public static QuotaWindow? Grab(string text, string label)
    {
        var m = Regex.Match(text, label + @"[^%]*?(\d+)\s*%\s*used\s*Resets\s*(.+?\))", Ci);
        if (!m.Success) return null;
        var resetsAt = ParseReset(m.Groups[2].Value);
        return resetsAt != null ? new QuotaWindow(double.Parse(m.Groups[1].Value), resetsAt.Value) : null;
    }

    [DllImport("kernel32.dll")] static extern bool SetStdHandle(int which, IntPtr handle);

    static string Tail(string t)
    {
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 200 ? t.Substring(t.Length - 200) : t;
    }

    static string? FindClaude()
    {
        var names = new[] { "claude.exe", "claude.cmd" };
        var dirs = new List<string>();
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim('"'), n); if (File.Exists(p)) return p; } catch { /* ignore */ }
            }
        return null;
    }

    public static ProbeResult Run(string cwd)
    {
        var claude = FindClaude();
        if (claude == null) return ProbeResult.Fail("找不到 claude（請確認 claude 在 PATH 裡）");
        var cmd = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"cmd.exe /c \"\"{claude}\" --settings \"{{\\\"disableAllHooks\\\":true}}\"\""
            : $"\"{claude}\" --settings \"{{\\\"disableAllHooks\\\":true}}\"";

        // 小克若是被 hook 以 stdio:ignore 啟動，自己的標準輸出入是空的 NUL；子程序會沿用它們而不是偽終端機，claude 就立刻結束。先清掉。
        SetStdHandle(-10, IntPtr.Zero); SetStdHandle(-11, IntPtr.Zero); SetStdHandle(-12, IntPtr.Zero);
        using var pty = ConPty.Start(cmd, cwd, 120, 40);
        var sw = Stopwatch.StartNew();
        long sentAt = -1;
        bool wrote2 = false;
        string last = "";
        while (sw.Elapsed < Timeout)
        {
            Thread.Sleep(250);
            var t = Strip(pty.Output());
            last = t;
            if (pty.HasExited) return ProbeResult.Fail("claude 提早結束：" + Tail(t));
            if (Trust.IsMatch(t)) return ProbeResult.Fail("untrusted: 請先在 " + cwd + " 開一次 claude 並信任該資料夾");
            if (Login.IsMatch(t)) return ProbeResult.Fail("login: 請在終端機執行 claude 並 /login");
            if (RateLimited.IsMatch(t)) return ProbeResult.Fail("官方額度端點限流中（rate limited），稍後再試");
            if (sentAt < 0 && Prompt.IsMatch(t))
            {
                sentAt = sw.ElapsedMilliseconds;
                Thread.Sleep(300);
                pty.Write("/usage");
            }
            else if (sentAt >= 0 && !wrote2 && sw.ElapsedMilliseconds - sentAt >= 1100)
            {
                wrote2 = true;
                pty.Write("\r");
            }
            var session = Grab(t, SessionLabel);
            var week = Grab(t, WeekLabel);
            if (session != null && week != null)
            {
                // 再等一下讓畫面更新完，取最後一次的數字
                Thread.Sleep(700);
                var t2 = Strip(pty.Output());
                return new ProbeResult(true, null, Grab(t2, SessionLabel) ?? session, Grab(t2, WeekLabel) ?? week);
            }
        }
        return ProbeResult.Fail("timeout：" + Tail(last));
    }
}

/// <summary>最小的 Windows 偽終端機（ConPTY）包裝：啟動一個程序、讀輸出、寫輸入、結束時一併收掉。</summary>
sealed class ConPty : IDisposable
{
    IntPtr hPC;
    IntPtr hProcess;
    FileStream? input;
    SafeFileHandle? outRead;
    readonly StringBuilder buf = new();
    Thread? reader;
    bool disposed;

    public string Output() { lock (buf) return buf.ToString(); }

    public bool HasExited => WaitForSingleObject(hProcess, 0) == 0;

    public void Write(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        input!.Write(b, 0, b.Length);
        input.Flush();
    }

    public static ConPty Start(string commandLine, string cwd, short cols, short rows)
    {
        var p = new ConPty();
        SafeFileHandle? inRead = null, inWrite = null, outWrite = null;
        IntPtr attrList = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0) || !CreatePipe(out p.outRead, out outWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out p.hPC);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole 失敗");
            // 這兩端交給偽終端機了，自己不需要
            inRead.Dispose(); inRead = null;
            outWrite.Dispose(); outWrite = null;

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)0x20016 /* PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE */, p.hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var si = new STARTUPINFOEX { lpAttributeList = attrList };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            var sb = new StringBuilder(commandLine);
            if (!CreateProcessW(null, sb, IntPtr.Zero, IntPtr.Zero, false, 0x80000 /* EXTENDED_STARTUPINFO_PRESENT */, IntPtr.Zero, cwd, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "啟動 claude 失敗");
            CloseHandle(pi.hThread);
            p.hProcess = pi.hProcess;

            p.input = new FileStream(inWrite, FileAccess.Write, 1, false);
            inWrite = null; // 由 FileStream 接管
            var read = new FileStream(p.outRead, FileAccess.Read, 4096, false);
            p.reader = new Thread(() =>
            {
                var dec = Encoding.UTF8.GetDecoder();
                var bytes = new byte[4096];
                var chars = new char[4096];
                try
                {
                    int n;
                    while ((n = read.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        int c = dec.GetChars(bytes, 0, n, chars, 0);
                        lock (p.buf) { p.buf.Append(chars, 0, c); if (p.buf.Length > 400_000) p.buf.Remove(0, 200_000); }
                    }
                }
                catch { /* 管線關了 */ }
            })
            { IsBackground = true, Name = "conpty-reader" };
            p.reader.Start();
            return p;
        }
        catch
        {
            inRead?.Dispose(); inWrite?.Dispose(); outWrite?.Dispose();
            p.Dispose();
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hProcess != IntPtr.Zero) { TerminateProcess(hProcess, 0); }
        // ClosePseudoConsole 在舊版 Windows 會等輸出被讀完才返回，所以放到另一個執行緒，讀取執行緒繼續抽乾管線
        var pc = hPC;
        var proc = hProcess;
        var inp = input;
        var outR = outRead;
        new Thread(() =>
        {
            try { if (pc != IntPtr.Zero) ClosePseudoConsole(pc); } catch { /* ignore */ }
            try { inp?.Dispose(); } catch { /* ignore */ }
            try { outR?.Dispose(); } catch { /* ignore */ }
            if (proc != IntPtr.Zero) CloseHandle(proc);
        })
        { IsBackground = true }.Start();
    }

    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, uint size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
