using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Forms = System.Windows.Forms;

namespace XiaoKe;

static class Program
{
    const string MutexName = @"Local\XiaoKe.cc.Single";
    const string PokeEventName = @"Local\XiaoKe.cc.Poke";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--verify-usage")) return VerifyUsage(args);
        if (args.Contains("--probe-once")) return ProbeOnce(args);

        Forms.Application.EnableVisualStyles();
        bool selfTest = args.Contains("--selftest") || Environment.GetEnvironmentVariable("XK_SELFTEST") != null;

        // 單一執行個體：已經有一隻在跑就叫她戳一下，自己退出
        using var mutex = new Mutex(true, selfTest ? MutexName + ".selftest" : MutexName, out bool first);
        if (!first)
        {
            try { using var ev = EventWaitHandle.OpenExisting(PokeEventName); ev.Set(); } catch { /* ignore */ }
            return 0;
        }

        // Electron 版還在跑（pid 檔指向 electron 程序）就不要同時開
        if (!selfTest && ElectronRunning(out int epid))
        {
            Forms.MessageBox.Show($"Electron 版小克還在執行（PID {epid}）。\n請先把它退出，再啟動 C# 版。", "小克", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            return 1;
        }

        var assets = Paths.FindAssets();
        if (assets == null)
        {
            Forms.MessageBox.Show("找不到 assets（角色圖和音效）。\n請先在專案上層資料夾執行：npm run assets\n再重新建置 C# 版。", "小克", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return 2;
        }

        // 畫面很單純、視窗又是逐像素透明（本來就走軟體合成）：關掉硬體加速可以不載入顯示卡驅動的 D3D 元件，省下大量私有記憶體
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        wpf.DispatcherUnhandledException += (_, e) => { Log.Error("ui", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Error("domain", ex); };

        var app = new PetApp(wpf, assets, args);
        using var pokeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PokeEventName);
        var reg = ThreadPool.RegisterWaitForSingleObject(pokeEvent, (_, _) => app.OnSecondInstance(), null, Timeout.Infinite, false);
        wpf.Startup += (_, _) => app.Start();
        wpf.Run();
        reg.Unregister(null);
        GC.KeepAlive(mutex);
        return 0;
    }

    static bool ElectronRunning(out int pid)
    {
        pid = 0;
        try
        {
            if (!int.TryParse(File.ReadAllText(Paths.PidFile).Trim(), out pid) || pid == Environment.ProcessId) return false;
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals("electron", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 驗證用：XiaoKe.exe --verify-usage 輸出檔 [--root 目錄] [--cache 快取檔] [--noscan]
    /// 用和正式版相同的邏輯算一次今日 / 近 7 天 / 本週期，存成 JSON，方便和 Electron 版 usage.js 對照。
    /// 預設用暫存快取（全量重掃），不會動到 %APPDATA%\xiaoke-cc 的快取。
    /// </summary>
    static int VerifyUsage(string[] args)
    {
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var outFile = Arg("--verify-usage");
        var root = Arg("--root");
        var cache = Arg("--cache");
        bool noScan = args.Contains("--noscan");
        var tmpCache = cache != "" ? cache : Path.Combine(Path.GetTempPath(), "xk-verify-cache-" + Environment.ProcessId + ".json");
        var cfg = Config.Load();
        var tr = new UsageTracker(Paths.PricingFile, tmpCache, root != "" ? root : null) { PeriodStart = () => cfg.PeriodStart() };
        var sw = Stopwatch.StartNew();
        if (!noScan) tr.Scan();
        var s = tr.GetStats();
        var result = new Dictionary<string, object?>
        {
            ["today"] = s.Today, ["todayMsgs"] = s.TodayMsgs, ["week"] = s.Week,
            ["days"] = s.Days.ToDictionary(d => d.Key, d => d.Value),
            ["byModel"] = s.ByModel.ToDictionary(d => d.Key, d => d.Value),
            ["period"] = s.Period, ["periodStart"] = s.PeriodStart,
            ["scanMs"] = sw.ElapsedMilliseconds,
        };
        File.WriteAllText(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (cache == "") { try { File.Delete(tmpCache); } catch { /* ignore */ } }
        return 0;
    }

    /// <summary>
    /// 驗證用：XiaoKe.exe --probe-once 輸出.json [--text 畫面文字檔]
    /// 不帶 --text：真的用 ConPTY 開一次 claude 讀 /usage；帶 --text：只用解析器解析那份文字（模擬畫面）。
    /// </summary>
    static int ProbeOnce(string[] args)
    {
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var outFile = Arg("--probe-once");
        var text = Arg("--text");
        ProbeResult r;
        if (text != "")
        {
            var t = ProbeRunner.Strip(File.ReadAllText(text));
            var sess = ProbeRunner.Grab(t, @"Current\s*session");
            var week = ProbeRunner.Grab(t, @"Current\s*week\s*\(all\s*models\)");
            r = sess != null && week != null ? new ProbeResult(true, null, 0, sess, week) : ProbeResult.Fail("解析不到");
        }
        else
        {
            var sw = Stopwatch.StartNew();
            r = ProbeRunner.Run(Directory.GetCurrentDirectory());
            Console.Error.WriteLine(sw.ElapsedMilliseconds);
        }
        File.WriteAllText(outFile, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
