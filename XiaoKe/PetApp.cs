using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace XiaoKe;

/// <summary>把設定、用量統計、視窗、系統匣、會話檢查接在一起。</summary>
sealed class PetApp
{
    public Config Cfg { get; } = Config.Load();
    public IOfficialUsage Official => official;
    public UsageStats? LastStats { get; private set; }

    readonly System.Windows.Application wpf;
    readonly ProbeOfficialUsage official;
    readonly Dispatcher ui;
    readonly string assets;

    PetWindow win = null!;
    QuickWindow? quick;
    UsageTracker tracker = null!;
    Forms.NotifyIcon? tray;
    Forms.ContextMenuStrip menu = null!;
    Icon? trayIcon;
    DispatcherTimer? sessionTimer;
    FileSystemWatcher? attnWatcher;
    bool quitting;

    // 跟著 Claude Code 開關
    static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);
    bool hadSession;
    int emptyChecks;
    bool leaving;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "XiaoKe";

    public PetApp(System.Windows.Application wpf, string assets)
    {
        this.wpf = wpf;
        this.assets = assets;
        ui = wpf.Dispatcher;
        official = new ProbeOfficialUsage(ui, () => Cfg.UseReal, _ => RefreshStats());
    }

    // ================= 啟動 / 結束 =================
    public void Start()
    {
        try { Directory.CreateDirectory(Paths.Data); File.WriteAllText(Paths.PidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)); } catch { /* ignore */ }

        win = new PetWindow(this, assets);
        win.Show();
        CreateTray();

        tracker = new UsageTracker(Paths.PricingFile, Paths.UsageCache);
        tracker.StatsChanged += s => ui.BeginInvoke(() => { LastStats = WithQuota(s); win.OnStats(LastStats); CheckBudget(LastStats); });
        tracker.Turn += t => ui.BeginInvoke(() => { win.OnTurn(t); official.Probe(false); });
        tracker.Start(10_000);
        var firstProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        firstProbe.Tick += (_, _) => { firstProbe.Stop(); official.Probe(true); };
        firstProbe.Start();

        win.OnConfig();
        var greet = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        greet.Tick += (_, _) => { greet.Stop(); win.Greet(); };
        greet.Start();

        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        trim.Tick += (_, _) => { trim.Stop(); Native.TrimWorkingSet(); };
        trim.Start();

        StartAttentionWatch();

        sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        sessionTimer.Tick += (_, _) => CheckSessions();
        sessionTimer.Start();
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        try { tracker?.Stop(); } catch { /* ignore */ }
        try { attnWatcher?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (File.Exists(Paths.PidFile) && File.ReadAllText(Paths.PidFile).Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                File.Delete(Paths.PidFile);
        }
        catch { /* ignore */ }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        trayIcon?.Dispose();
        wpf.Shutdown();
    }

    public void OnSecondInstance() => ui.BeginInvoke(() => { if (!quitting) win.Poke(); });

    // ================= 用量 / 額度 =================
    UsageStats WithQuota(UsageStats st) =>
        st with { Rl = Official.Current(), RlErr = Cfg.UseReal ? official.LastError : null, RlTried = official.Tried };

    /// <summary>點量槽時順便讀一次官方額度（至少隔 20 秒）。</summary>
    public void ProbeNow() => official.Probe(true);

    void RefreshStats()
    {
        if (tracker == null) return;
        LastStats = WithQuota(tracker.GetStats());
        win.OnStats(LastStats);
    }

    void SetCfg(Action<Config> patch)
    {
        patch(Cfg);
        Cfg.Save();
        win.OnConfig();
    }

    /// <summary>所有音效（按壓、每輪結束）共用一個開關。</summary>
    public void PlaySound(string file) { if (Cfg.Sound) Sound.Play(assets, file, Cfg.Volume); }

    // ================= 快速輸入框 =================
    public void OpenQuick()
    {
        if (quick != null) { quick.Activate(); return; }
        var wa = Forms.Screen.FromHandle(win.Hwnd).WorkingArea;
        double s = win.Scale;
        int qw = (int)Math.Round(640 * s);
        int x = wa.X + (wa.Width - qw) / 2;
        int y = wa.Y + (int)Math.Round(wa.Height * 0.32);
        quick = new QuickWindow(this, x, y, s);
        quick.Closed += (_, _) => quick = null;
        quick.Show();
    }

    public void SubmitQuick(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 8000)
        {
            int n = 8000;
            if (char.IsHighSurrogate(text[n - 1])) n--;
            text = text.Substring(0, n);
        }
        try
        {
            Process.Start(new ProcessStartInfo("claude://claude.ai/new?q=" + Uri.EscapeDataString(text)) { UseShellExecute = true });
            win.SayText("交給 Claude 啦～", 3);
        }
        catch { win.SayText("開不了 Claude 桌面版...", 3); }
    }

    // ================= 每日預算提醒 =================
    void CheckBudget(UsageStats st)
    {
        if (!Cfg.BudgetAlert || Cfg.DailyBudget <= 0 || st.Today < Cfg.DailyBudget) return;
        string key = UsageTracker.LocalDate(DateTimeOffset.Now.ToUnixTimeMilliseconds()) + "@" + Cfg.DailyBudget.ToString(CultureInfo.InvariantCulture);
        if (Cfg.BudgetAlertedKey == key) return;
        Cfg.BudgetAlertedKey = key;
        Cfg.Save();
        win.OnBudget(st.Today, Cfg.DailyBudget);
    }

    // ================= 需要你批准時提醒 =================
    // hook.js notify 在 attention\ 下放 <會話>.json，處理完（clear）就刪掉；這裡監看那個資料夾。
    static readonly TimeSpan AttentionFresh = TimeSpan.FromMinutes(10);

    void StartAttentionWatch()
    {
        try
        {
            Directory.CreateDirectory(Paths.Attention);
            attnWatcher = new FileSystemWatcher(Paths.Attention, "*.json") { EnableRaisingEvents = true };
            attnWatcher.Created += (_, _) => ui.BeginInvoke(CheckAttention);
            attnWatcher.Changed += (_, _) => ui.BeginInvoke(CheckAttention);
            attnWatcher.Deleted += (_, _) => ui.BeginInvoke(CheckAttention);
            CheckAttention();
        }
        catch (Exception e) { Log.Error("attention", e); }
    }

    string? shownAttention;

    void CheckAttention()
    {
        string? newest = null; long newestT = 0; string type = "", msg = "", cwd = "";
        try
        {
            foreach (var f in Directory.GetFiles(Paths.Attention, "*.json"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > AttentionFresh) { File.Delete(f); continue; }
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    long t = r.TryGetProperty("t", out var tv) && tv.TryGetInt64(out var tl) ? tl : 0;
                    if (newest != null && t < newestT) continue;
                    newest = Path.GetFileName(f) + ":" + t; newestT = t;
                    type = r.TryGetProperty("type", out var a) ? a.GetString() ?? "" : "";
                    msg = r.TryGetProperty("message", out var b) ? b.GetString() ?? "" : "";
                    cwd = r.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
                }
                catch { /* 檔案正被寫，下一次事件會再來 */ }
            }
        }
        catch { return; }
        if (newest == null) { if (shownAttention != null) { shownAttention = null; win.ClearAttention(); } return; }
        if (newest == shownAttention) return;
        shownAttention = newest;
        win.OnAttention(type, msg, cwd);
    }

    // ================= 跟著 Claude Code 開關 =================
    // hook.js 在 sessions\ 下為每個 Claude Code 會話放一個檔案，會話結束時刪掉。
    // 檔案和對話記錄都超過 2 小時沒動靜的，當作已關閉（兜底 Claude Code 異常退出沒觸發 SessionEnd）。
    // 曾經有過會話、現在一個都不剩 → 小克下班。手動啟動且從沒有會話時不會自動退出。
    int LiveSessions()
    {
        string[] files;
        try { files = Directory.GetFiles(Paths.Sessions, "*.json"); } catch { return 0; }
        int n = 0;
        bool? cli = null;
        foreach (var f in files)
        {
            try
            {
                var last = File.GetLastWriteTimeUtc(f);
                using (var doc = JsonDocument.Parse(File.ReadAllText(f)))
                {
                    if (doc.RootElement.TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.String && tr.GetString() is { Length: > 0 } tp)
                    {
                        try { var m = File.GetLastWriteTimeUtc(tp); if (m > last && File.Exists(tp)) last = m; } catch { /* 記錄還沒生成 */ }
                    }
                }
                var age = DateTime.UtcNow - last;
                if (age > StaleAfter) File.Delete(f);
                // 有 Claude Code 程序在跑：2 小時內的都算在線；完全沒有（整個 Claude 都關了）：只有 1 分鐘內剛動過的才算
                // （SessionEnd 沒觸發時不必等 2 小時）
                else if ((cli ??= CliRunning()) || age <= RecentWindow) n++;
            }
            catch { /* 檔案正被寫 */ }
        }
        return n;
    }

    /// <summary>有沒有 Claude Code（CLI）程序在跑。桌面版本體是 WindowsApps 底下的 Claude.exe，要排除。</summary>
    static bool CliRunning()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null) return true; // 讀不到路徑就保守當作有
                if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFileName(path), "Claude.exe", StringComparison.Ordinal)) continue; // 桌面版（大寫 C），CLI 是小寫 claude.exe
                return true;
            }
            catch { return true; }
            finally { p.Dispose(); }
        }
        return false;
    }

    void CheckSessions()
    {
        if (leaving) return;
        int n = LiveSessions();
        if (n > 0) { hadSession = true; emptyChecks = 0; return; }
        if (!Cfg.FollowClaude || !hadSession) return;
        if (++emptyChecks < 2) return;
        leaving = true;
        win.Bye();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        t.Tick += (_, _) => { t.Stop(); Quit(); };
        t.Start();
    }

    // ================= 系統匣 / 選單 =================
    void CreateTray()
    {
        menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu();
        try
        {
            var sz = Forms.SystemInformation.SmallIconSize;
            using var src = new Bitmap(Path.Combine(assets, "xiaoke1.png"));
            using var bm = new Bitmap(sz.Width, sz.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bm))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, sz.Width, sz.Height);
            }
            var h = bm.GetHicon();
            using (var tmp = Icon.FromHandle(h)) trayIcon = (Icon)tmp.Clone();
            DestroyIcon(h);
        }
        catch { trayIcon = SystemIcons.Application; }
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "小克 · Claude Code 記賬", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ui.BeginInvoke(() => win.Poke()); };
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public void ShowMenu()
    {
        Native.SetForegroundWindow(win.Hwnd);
        menu.Show(Forms.Control.MousePosition);
    }

    static bool AutostartOn()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) != null; } catch { return false; }
    }

    static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue(RunName, false);
        }
        catch { /* ignore */ }
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* ignore */ }
    }

    Forms.ToolStripMenuItem Add(Forms.ToolStripItemCollection to, string text, Action click, bool? check = null, bool enabled = true)
    {
        var it = new Forms.ToolStripMenuItem(text) { Enabled = enabled };
        if (check != null) it.Checked = check.Value;
        it.Click += (_, _) => click();
        to.Add(it);
        return it;
    }

    void BuildMenu()
    {
        var m = menu.Items;
        m.Clear();
        Add(m, "看看賬本", () => win.ShowStatsBubble());
        Add(m, "問 Claude…（雙擊小克）", OpenQuick);
        Add(m, "讀取官方額度（Claude Code /usage）", () => { SetCfg(c => c.UseReal = !c.UseReal); if (Cfg.UseReal) official.Probe(true); else RefreshStats(); }, Cfg.UseReal);
        Add(m, "顯示每週量槽", () => SetCfg(c => c.ShowWeek = !c.ShowWeek), Cfg.ShowWeek);
        Add(m, "顯示 5 小時量槽", () => SetCfg(c => c.ShowFive = !c.ShowFive), Cfg.ShowFive);

        var show = new Forms.ToolStripMenuItem("量槽顯示方式");
        Add(show.DropDownItems, "已用（同 Claude）", () => SetCfg(c => c.ShowUsed = true), Cfg.ShowUsed);
        Add(show.DropDownItems, "剩餘", () => SetCfg(c => c.ShowUsed = false), !Cfg.ShowUsed);
        m.Add(show);

        Add(m, "戳一下", () => win.Poke());
        m.Add(new Forms.ToolStripSeparator());

        var size = new Forms.ToolStripMenuItem("大小");
        foreach (var s in new[] { 0.7, 0.85, 1, 1.2, 1.4 })
        {
            double sv = s;
            Add(size.DropDownItems, $"{Math.Round(s * 100)}%", () => SetCfg(c => c.Scale = sv), Cfg.Scale == s);
        }
        m.Add(size);

        Add(m, "吸附螢幕邊緣（左右靠邊翻轉、上下不跑出畫面）", () => SetCfg(c => { c.SnapEdge = !c.SnapEdge; if (!c.SnapEdge) c.SnapSide = null; }), Cfg.SnapEdge);
        Add(m, "音效", () => SetCfg(c => c.Sound = !c.Sound), Cfg.Sound);
        var budget = new Forms.ToolStripMenuItem("每日預算提醒");
        Add(budget.DropDownItems, "啟用（今日花費超過預算時提醒）", () => SetCfg(c => c.BudgetAlert = !c.BudgetAlert), Cfg.BudgetAlert);
        budget.DropDownItems.Add(new Forms.ToolStripSeparator());
        foreach (var b in new[] { 1.0, 2, 5, 10, 20, 50, 100 })
        {
            double bv = b;
            Add(budget.DropDownItems, $"預算 ${b:0}", () => SetCfg(c => c.DailyBudget = bv), Cfg.DailyBudget == b);
        }
        m.Add(budget);
        Add(m, "需要我批准時提醒（氣泡加聲音）", () => SetCfg(c => c.AttentionBubble = !c.AttentionBubble), Cfg.AttentionBubble);
        Add(m, "每輪結束冒泡", () => SetCfg(c => c.TurnBubble = !c.TurnBubble), Cfg.TurnBubble);
        Add(m, "跟著 Claude Code 開關", () => SetCfg(c => c.FollowClaude = !c.FollowClaude), Cfg.FollowClaude);
        Add(m, "開機自啟", () => SetAutostart(!AutostartOn()), AutostartOn());
        m.Add(new Forms.ToolStripSeparator());

        Add(m, "編輯臺詞 (lines.json)", () => OpenFile(Paths.LinesFile));
        Add(m, "編輯價格 (pricing.json)", () => OpenFile(Paths.PricingFile));
        Add(m, "重新載入臺詞和價格", () => { tracker.Reload(); win.LoadLines(); win.SayText("臺詞和價格重新載入好啦", 3); });
        Add(m, "回到右下角", () => { win.ResetPosition(); SetCfg(c => { c.X = null; c.Y = null; }); });
        m.Add(new Forms.ToolStripSeparator());
        Add(m, "退出", Quit);
    }
}
