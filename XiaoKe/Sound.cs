using System;
using System.IO;
using System.Windows.Media;

namespace XiaoKe;

/// <summary>播放 assets 裡的短音效（mp3 / wav），每次一個 MediaPlayer，播完就關。</summary>
static class Sound
{
    public static void Play(string assetsDir, string file, double volume)
    {
        try
        {
            var path = Path.Combine(assetsDir, file);
            if (!File.Exists(path)) return;
            var p = new MediaPlayer { Volume = Math.Max(0, Math.Min(1, volume)) };
            void Done(object? s, EventArgs e) { try { p.Close(); } catch { /* ignore */ } }
            p.MediaEnded += Done;
            p.MediaFailed += (s, e) => { Log.Error("sound " + file, e.ErrorException); Done(s, EventArgs.Empty); };
            p.Open(new Uri(path));
            p.Play();
        }
        catch { /* 沒聲音也不影響 */ }
    }
}
