using System;
using System.Runtime.InteropServices;

namespace XiaoKe;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>把閒置頁面還給系統（工作集變小，用到時再自動載回）。</summary>
    public static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { /* ignore */ }
    }

    /// <summary>主螢幕的 DPI 縮放比例（1.0 = 100%）。</summary>
    public static double PrimaryScale()
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0) return dx / 96.0;
        }
        catch { /* ignore */ }
        return 1.0;
    }
}
