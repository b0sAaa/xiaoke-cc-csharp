using System;
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
        Forms.Application.EnableVisualStyles();

        // 單一執行個體：已經有一隻在跑就叫她戳一下，自己退出
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try { using var ev = EventWaitHandle.OpenExisting(PokeEventName); ev.Set(); } catch { /* ignore */ }
            return 0;
        }

        var assets = Paths.FindAssets();
        if (assets == null)
        {
            Forms.MessageBox.Show("找不到 assets（角色圖和音效）。\n請先在專案上層資料夾執行：npm run assets\n再重新建置。", "小克", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return 2;
        }

        // 畫面很單純、視窗又是逐像素透明（本來就走軟體合成）：關掉硬體加速可以不載入顯示卡驅動的 D3D 元件，省下大量私有記憶體
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        wpf.DispatcherUnhandledException += (_, e) => { Log.Error("ui", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Error("domain", ex); };

        var app = new PetApp(wpf, assets);
        using var pokeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PokeEventName);
        var reg = ThreadPool.RegisterWaitForSingleObject(pokeEvent, (_, _) => app.OnSecondInstance(), null, Timeout.Infinite, false);
        wpf.Startup += (_, _) => app.Start();
        wpf.Run();
        reg.Unregister(null);
        GC.KeepAlive(mutex);
        return 0;
    }
}
