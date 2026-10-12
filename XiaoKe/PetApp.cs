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
    readonly bool selfTest;
    readonly string[] args;

    PetWindow win = null!;
    QuickWindow? quick;
    UsageTracker tracker = null!;
    Forms.NotifyIcon? tray;
    Forms.ContextMenuStrip menu = null!;
    Icon? trayIcon;
    DispatcherTimer? sessionTimer;
    bool quitting;

    // 跟著 Claude Code 開關
    static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);
    static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);
    bool hadSession;
    int emptyChecks;
    bool leaving;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "XiaoKe";
    static readonly string[] DayNames = { "週日", "週一", "週二", "週三", "週四", "週五", "週六" };

    public PetApp(System.Windows.Application wpf, string assets, string[] args)
    {
        this.wpf = wpf;
        this.assets = assets;
        this.args = args;
        ui = wpf.Dispatcher;
        official = new ProbeOfficialUsage(ui, () => Cfg.UseReal, ok => { if (ok) OnRealUpdate(); else RefreshStats(); });
        selfTest = args.Contains("--selftest") || Environment.GetEnvironmentVariable("XK_SELFTEST") != null;
    }

    // ================= 啟動 / 結束 =================
    public void Start()
    {
        if (!selfTest)
        {
            try { Directory.CreateDirectory(Paths.Data); File.WriteAllText(Paths.PidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)); } catch { /* ignore */ }
        }

        win = new PetWindow(this, assets);
        win.Show();
        CreateTray();

        tracker = new UsageTracker(Paths.PricingFile, Paths.UsageCache) { PeriodStart = () => Cfg.PeriodStart() };
        tracker.StatsChanged += s => ui.BeginInvoke(() => { LastStats = WithQuota(s); win.OnStats(LastStats); });
        tracker.Turn += t => ui.BeginInvoke(() => { if (Environment.GetEnvironmentVariable("XK_DEBUG") != null) Log.Info($"turn cost={t.Cost} cwd={t.Cwd}"); win.OnTurn(t); official.Probe(false); });
        tracker.Start(10_000);
        var firstProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        firstProbe.Tick += (_, _) => { firstProbe.Stop(); if (!selfTest) official.Probe(true); };
        firstProbe.Start();

        win.OnConfig();
        var greet = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        greet.Tick += (_, _) => { greet.Stop(); win.Greet(); };
        greet.Start();

        if (Environment.GetEnvironmentVariable("XK_MEMLOG") is { Length: > 0 } memLog)
        {
            var mt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            mt.Tick += (_, _) =>
            {
                var p = Process.GetCurrentProcess();
                var gi = GC.GetGCMemoryInfo();
                File.AppendAllText(memLog, $"{DateTime.Now:HH:mm:ss} ws={p.WorkingSet64 >> 20}MB priv={p.PrivateMemorySize64 >> 20}MB managed={GC.GetTotalMemory(false) >> 20}MB heap={gi.HeapSizeBytes >> 20}MB gen2={GC.CollectionCount(2)}{Environment.NewLine}");
            };
            mt.Start();
        }

        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        trim.Tick += (_, _) => { trim.Stop(); Native.TrimWorkingSet(); };
        trim.Start();

        if (selfTest) { _ = RunSelfTest(); return; }
        sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        sessionTimer.Tick += (_, _) => CheckSessions();
        sessionTimer.Start();
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        try { tracker?.Stop(); } catch { /* ignore */ }
        try
        {
            if (!selfTest && File.Exists(Paths.PidFile) && File.ReadAllText(Paths.PidFile).Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                File.Delete(Paths.PidFile);
        }
        catch { /* ignore */ }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        trayIcon?.Dispose();
        wpf.Shutdown();
    }

    public void OnSecondInstance() => ui.BeginInvoke(() => { if (!quitting) win.Poke(); });

    // ================= 用量 / 額度 =================
    UsageStats WithQuota(UsageStats st)
    {
        if (!(Cfg.WeeklyBudget > 0) && Cfg.CalibrateUsedPct > 0 && st.Period > 0)
        {
            Cfg.WeeklyBudget = Ui.JsRound(st.Period / (Cfg.CalibrateUsedPct.Value / 100));
            Cfg.CalibrateUsedPct = null;
            Cfg.Save();
        }
        return st with
        {
            Budget = Cfg.WeeklyBudget,
            NextReset = Cfg.PeriodStart() + 7 * 86400_000L,
            Rl = Official.Current(),
            RlErr = Cfg.UseReal ? official.LastError : null,
        };
    }

    /// <summary>點量槽時順便讀一次官方額度（至少隔 20 秒）。</summary>
    public void ProbeNow() => official.Probe(true);

    void RefreshStats()
    {
        if (tracker == null) return;
        LastStats = WithQuota(tracker.GetStats());
        win.OnStats(LastStats);
    }

    // 讀到官方數字：重置時間跟官方一致，並用官方百分比校準估算（讀不到官方數字時退回估算也比較準）
    void OnRealUpdate()
    {
        var w = official.Raw?.Week;
        if (w != null)
        {
            // 分鐘四捨五入到整點，例如 13:59 → 14:00
            var d = DateTimeOffset.FromUnixTimeMilliseconds(w.ResetsAt + 30 * 60_000L).ToLocalTime();
            if ((int)d.DayOfWeek != Cfg.ResetDay || d.Hour != Cfg.ResetHour) { Cfg.ResetDay = (int)d.DayOfWeek; Cfg.ResetHour = d.Hour; }
            if (w.Used >= 10)
            {
                double period = tracker.GetStats().Period;
                if (period > 0) Cfg.WeeklyBudget = Ui.JsRound(period / (w.Used / 100));
            }
            Cfg.Save();
        }
        RefreshStats();
    }

    public void Calibrate(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return;
        if (!(pct > 0 && pct <= 100) || LastStats == null || !(LastStats.Period > 0)) return;
        Cfg.WeeklyBudget = Ui.JsRound(LastStats.Period / (pct / 100));
        Cfg.Save();
        LastStats = WithQuota(LastStats);
        win.OnStats(LastStats);
    }

    void SetCfg(Action<Config> patch)
    {
        patch(Cfg);
        Cfg.Save();
        win.OnConfig();
    }

    // 改額度重置時間：存設定、用新的週期重算本週已花，馬上更新量槽
    void SetReset(Action<Config> patch)
    {
        SetCfg(patch);
        LastStats = WithQuota(tracker.GetStats());
        win.OnStats(LastStats);
    }

    public void PlaySound(string file) => Sound.Play(assets, file, Cfg.Volume);

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
        // 讀得到官方數字時會自動校準估算，這項只在讀不到時才需要
        if (Official.Current() == null) Add(m, "校準每週額度…", () => win.CalibrateUi());
        Add(m, "讀取官方額度（Claude Code /usage）", () => { SetCfg(c => c.UseReal = !c.UseReal); if (Cfg.UseReal) official.Probe(true); else RefreshStats(); }, Cfg.UseReal);
        Add(m, "顯示 5 小時量槽", () => SetCfg(c => c.ShowFive = !c.ShowFive), Cfg.ShowFive);

        var show = new Forms.ToolStripMenuItem("量槽顯示方式");
        Add(show.DropDownItems, "已用（同 Claude）", () => SetCfg(c => c.ShowUsed = true), Cfg.ShowUsed);
        Add(show.DropDownItems, "剩餘", () => SetCfg(c => c.ShowUsed = false), !Cfg.ShowUsed);
        m.Add(show);
        Add(m, "立即更新官方額度", () => official.Probe(true), enabled: Cfg.UseReal);

        var reset = new Forms.ToolStripMenuItem($"額度重置時間（{DayNames[Cfg.ResetDay]} {Cfg.ResetHour:D2}:00）");
        var days = new Forms.ToolStripMenuItem("星期");
        for (int i = 0; i < 7; i++) { int d = i; Add(days.DropDownItems, DayNames[i], () => SetReset(c => c.ResetDay = d), Cfg.ResetDay == i); }
        var hours = new Forms.ToolStripMenuItem("時間");
        for (int i = 0; i < 24; i++) { int h = i; Add(hours.DropDownItems, $"{i:D2}:00", () => SetReset(c => c.ResetHour = h), Cfg.ResetHour == i); }
        reset.DropDownItems.Add(days);
        reset.DropDownItems.Add(hours);
        m.Add(reset);

        Add(m, "戳一下", () => win.Poke());
        m.Add(new Forms.ToolStripSeparator());

        var size = new Forms.ToolStripMenuItem("大小");
        foreach (var s in new[] { 0.7, 0.85, 1, 1.2, 1.4 })
        {
            double sv = s;
            Add(size.DropDownItems, $"{Math.Round(s * 100)}%", () => SetCfg(c => c.Scale = sv), Cfg.Scale == s);
        }
        m.Add(size);

        var cur = new Forms.ToolStripMenuItem("貨幣");
        Add(cur.DropDownItems, "美元 $", () => SetCfg(c => c.Currency = "USD"), Cfg.Currency == "USD");
        Add(cur.DropDownItems, $"人民幣 ¥（匯率 {Cfg.CnyRate.ToString(CultureInfo.InvariantCulture)}）", () => SetCfg(c => c.Currency = "CNY"), Cfg.Currency == "CNY");
        m.Add(cur);

        Add(m, "按壓音效", () => SetCfg(c => c.Sound = !c.Sound), Cfg.Sound);
        Add(m, "每輪結束冒泡", () => SetCfg(c => c.TurnBubble = !c.TurnBubble), Cfg.TurnBubble);
        Add(m, "每輪結束提示音", () => SetCfg(c => c.TurnSound = !c.TurnSound), Cfg.TurnSound);
        Add(m, "試聽提示音（叮）", () => PlaySound("task-end-a.wav"));
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

    // ================= 自檢：依次截圖「待機 / 戳一下 / 賬本 / 一輪結束 / 校準 / 快速輸入框」 =================
    async Task RunSelfTest()
    {
        var dir = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                  ?? Environment.GetEnvironmentVariable("XK_SELFTEST") ?? Path.Combine(Paths.AppDir, "selftest");
        if (dir == "1") dir = Path.Combine(Paths.AppDir, "selftest");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 60 && LastStats == null; i++) await Task.Delay(1000);
        await Task.Delay(1500);
        win.Capture(Path.Combine(dir, "1-idle.png"));
        win.Poke(); await Task.Delay(800); win.Capture(Path.Combine(dir, "2-poke.png"));
        win.ShowStatsBubble(); await Task.Delay(800); win.Capture(Path.Combine(dir, "3-stats.png"));
        win.OnTurn(new TurnInfo("x", 0.4213, "claude-opus-5-5", "D:/projects/demo")); await Task.Delay(800); win.Capture(Path.Combine(dir, "4-turn.png"));
        win.CalibrateUi(); await Task.Delay(800); win.Capture(Path.Combine(dir, "5-calibrate.png"));
        OpenQuick(); await Task.Delay(500);
        if (quick != null) Ui.Capture(quick, Path.Combine(dir, "6-quick.png"));
        Quit();
    }
}
