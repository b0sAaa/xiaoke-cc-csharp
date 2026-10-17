using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace XiaoKe;

/// <summary>小克本體：無邊框透明置頂視窗，角色圖 + 量槽 + 對話氣泡。</summary>
sealed class PetWindow : Window
{
    public const int W = 300;
    public const int H = 440;

    readonly PetApp app;
    Config Cfg => app.Cfg;
    IntPtr hwnd;

    // 視覺元素
    readonly Grid root = new();
    readonly StackPanel stage = new();
    readonly Image charImg = new();
    readonly ScaleTransform charScale = new(1, 1);
    readonly ScaleTransform flip = new(1, 1); // 吸在左邊時水平翻轉
    double opaqueL, opaqueR = 1, opaqueT; // 另含上緣
    double _unusedOpaque; // 角色圖不透明部分的左右邊界（佔圖寬的比例）
    double sideMargin; bool sideLeft;
    readonly Border badge = new();
    readonly StackPanel fiveBox = new();
    readonly StackPanel weekBox = new();
    readonly TextBlock pct5, pct;
    readonly Border fill5, fill;
    readonly Grid bubbleHost = new();
    readonly Border bubbleBody = new();
    readonly Border bubbleTail = new();
    readonly ScaleTransform bubbleScale = new(0.6, 0.6);
    const double TrackW = 176 - 3 - 18; // 量槽寬 - 邊框 - 左右內距

    // 狀態
    UsageStats? stats;
    double curH = H; // 目前視窗高度（DIP）：氣泡太高時往上加高、底部位置不動
    bool shown;
    bool ignoring;
    double wantH = -1;
    double stageH;
    Drag? drag;
    DispatcherTimer? clickTimer, pressTimer;
    readonly DispatcherTimer hideTimer = new(), fitTimer = new(), hitTimer = new(DispatcherPriority.Background);

    // 角色圖不透明區域（alpha>30，1/4 降取樣）
    bool[]? mask;
    int maskW, maskH;

    // 臺詞
    List<(string Text, double Weight)> lines = new();
    int lastIdx = -1;
    static readonly Random Rng = new();
    static readonly string[] PokeSounds = { "D1.mp3", "D2.mp3", "Ya1.mp3", "Ya2.mp3" };

    sealed class Drag
    {
        public Native.POINT C;
        public int X, Bottom;
        public bool Moved;
    }

    public IntPtr Hwnd => hwnd;
    public double Scale => hwnd == IntPtr.Zero ? 1.0 : Native.GetDpiForWindow(hwnd) / 96.0;

    public PetWindow(PetApp app, string assetsDir)
    {
        this.app = app;
        Title = "小克";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = W;
        Height = H;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; // 先放到螢幕外，SourceInitialized 再定位，避免閃一下

        // ---- 角色圖 ----
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(Path.Combine(assetsDir, "xiaoke1.png"));
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.EndInit();
        bmp.Freeze();
        BuildMask(bmp);
        charImg.Source = bmp;
        charImg.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(charImg, BitmapScalingMode.Fant);
        charImg.RenderTransformOrigin = new Point(0.5, 1);
        var tg = new TransformGroup();
        tg.Children.Add(charScale);
        tg.Children.Add(flip);
        charImg.RenderTransform = tg;
        charImg.Effect = Ui.Shadow(4, 3, 0.25);
        charImg.Cursor = Cursors.Hand;
        charImg.MouseLeftButtonDown += CharDown;
        charImg.MouseMove += CharMove;
        charImg.MouseLeftButtonUp += (_, e) => EndDrag();
        charImg.LostMouseCapture += (_, _) => EndDrag();

        // ---- 量槽 ----
        badge.Width = 176;
        badge.Padding = new Thickness(9, 5, 9, 7);
        badge.BorderThickness = new Thickness(1.5);
        badge.BorderBrush = Ui.BadgeBorder;
        badge.CornerRadius = new CornerRadius(10);
        badge.Background = Ui.Sand;
        badge.Cursor = Cursors.Hand;
        badge.Margin = new Thickness(0, -16, 0, 0);
        badge.Effect = Ui.Shadow(6, 2, 0.16);
        ToolTipService.SetInitialShowDelay(badge, 500);
        ToolTipService.SetShowDuration(badge, 30000);
        var badgeStack = new StackPanel();
        (pct5, fill5) = MakeBar("5-hour", out var fiveRow, out var fiveTrack);
        fiveBox.Children.Add(fiveRow);
        fiveBox.Children.Add(fiveTrack);
        fiveBox.Margin = new Thickness(0, 0, 0, 6);
        fiveBox.Visibility = Visibility.Collapsed;
        badgeStack.Children.Add(fiveBox);
        (pct, fill) = MakeBar("Weekly · all models", out var wRow, out var wTrack);
        weekBox.Children.Add(wRow);
        weekBox.Children.Add(wTrack);
        badgeStack.Children.Add(weekBox);
        badge.Child = badgeStack;
        badge.MouseLeftButtonUp += (_, e) => { OnBadgeClick(); e.Handled = true; };

        stage.HorizontalAlignment = HorizontalAlignment.Center;
        stage.VerticalAlignment = VerticalAlignment.Bottom;
        stage.Margin = new Thickness(0, 0, 0, 4);
        stage.Children.Add(charImg);
        stage.Children.Add(badge);

        // ---- 氣泡 ----
        bubbleBody.Background = Ui.Sand;
        bubbleBody.BorderBrush = Ui.Coral;
        bubbleBody.BorderThickness = new Thickness(3);
        bubbleBody.CornerRadius = new CornerRadius(16);
        bubbleBody.Padding = new Thickness(14, 10, 14, 10);
        bubbleBody.MinWidth = 90;
        bubbleBody.MaxWidth = 260;
        bubbleBody.Effect = Ui.Shadow(8, 3, 0.18);
        bubbleBody.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside<TextBox>(d)) return;
            HideBubble();
        };
        bubbleTail.Width = bubbleTail.Height = 19;
        bubbleTail.Background = Ui.Sand;
        bubbleTail.BorderBrush = Ui.Coral;
        bubbleTail.BorderThickness = new Thickness(0, 0, 3, 3);
        bubbleTail.HorizontalAlignment = HorizontalAlignment.Center;
        bubbleTail.VerticalAlignment = VerticalAlignment.Bottom;
        bubbleTail.Margin = new Thickness(0, 0, 0, -8);
        bubbleTail.RenderTransformOrigin = new Point(0.5, 0.5);
        bubbleTail.RenderTransform = new RotateTransform(45);
        bubbleHost.HorizontalAlignment = HorizontalAlignment.Center;
        bubbleHost.VerticalAlignment = VerticalAlignment.Bottom;
        bubbleHost.Opacity = 0;
        bubbleHost.IsHitTestVisible = false;
        bubbleHost.RenderTransformOrigin = new Point(0.5, 1);
        bubbleHost.RenderTransform = bubbleScale;
        bubbleHost.Children.Add(bubbleBody);
        bubbleHost.Children.Add(bubbleTail);

        root.Children.Add(bubbleHost);
        root.Children.Add(stage);
        Content = root;

        MouseRightButtonUp += (_, e) => { app.ShowMenu(); e.Handled = true; };

        hideTimer.Tick += (_, _) => { hideTimer.Stop(); HideBubble(); };
        fitTimer.Tick += (_, _) => { fitTimer.Stop(); RunFit(); };
        hitTimer.Interval = TimeSpan.FromMilliseconds(150);
        hitTimer.Tick += (_, _) => HitTick();

        LoadLines();
        ApplyScale();
    }

    // ================= 初始化 / 位置 =================
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        PlaceInitial();
        ignoring = false;
        SetIgnore(true);
        hitTimer.Start();
    }

    void PlaceInitial()
    {
        double ps = Native.PrimaryScale();
        int wP = (int)Math.Round(W * ps), hP = (int)Math.Round(H * ps);
        var wa = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        int x = wa.X + wa.Width - wP - (int)Math.Round(20 * ps);
        int y = wa.Y + wa.Height - hP;
        // 存下來的位置如果已經不在任何螢幕內（拔了螢幕、拖到畫面外），就回到右下角
        if (Cfg.X != null && Cfg.Y != null)
        {
            int sx = (int)Math.Round(Cfg.X.Value * ps), sy = (int)Math.Round(Cfg.Y.Value * ps);
            int cx = sx + wP / 2, cy = sy + hP / 2;
            if (Forms.Screen.AllScreens.Any(s => cx >= s.WorkingArea.X && cx <= s.WorkingArea.Right && cy >= s.WorkingArea.Y && cy <= s.WorkingArea.Bottom))
            { x = sx; y = sy; }
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, wP, hP, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 實際所在螢幕的縮放可能和主螢幕不同：以目前縮放重算尺寸，底部與左邊不動
        if (Native.GetWindowRect(hwnd, out var r)) SetBounds(r.Left, r.Bottom, curH);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (drag == null && Native.GetWindowRect(hwnd, out var r)) SetBounds(r.Left, r.Bottom, curH);
    }

    void SetBounds(int xPhys, int bottomPhys, double hDip)
    {
        double s = Scale;
        int wP = (int)Math.Round(W * s), hP = (int)Math.Round(hDip * s);
        Native.SetWindowPos(hwnd, IntPtr.Zero, xPhys, bottomPhys - hP, wP, hP, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>回到主螢幕工作區右下角。</summary>
    public void ResetPosition()
    {
        double s = Scale;
        var wa = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        curH = H;
        wantH = -1;
        Cfg.SnapSide = null;
        Cfg.Mirrored = false; // 右下角本來就是面向左的預設樣子
        ApplySnapLayout();
        int wP = (int)Math.Round(W * s), hP = (int)Math.Round(H * s);
        Native.SetWindowPos(hwnd, IntPtr.Zero, wa.X + wa.Width - wP - (int)Math.Round(20 * s), wa.Y + wa.Height - hP, wP, hP, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (shown) ScheduleFit();
    }

    /// <summary>氣泡太高放不下時，視窗往上加高（need 為需要的總高度 DIP，0 = 還原）。</summary>
    void Fit(double need)
    {
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        double s = Scale;
        var wa = Forms.Screen.FromHandle(hwnd).WorkingArea;
        double target = Math.Min(Math.Max(H, Math.Ceiling(need)), wa.Height / s);
        if (target == curH) return;
        curH = target;
        SetBounds(r.Left, r.Bottom, target);
    }

    void ScheduleFit()
    {
        fitTimer.Stop();
        fitTimer.Interval = TimeSpan.FromMilliseconds(shown ? 1 : 250);
        fitTimer.Start();
    }

    void RunFit()
    {
        double need = 0;
        if (shown)
        {
            bubbleBody.Measure(new Size(W, double.PositiveInfinity));
            need = stageH + bubbleBody.DesiredSize.Height + 30;
        }
        if (need != wantH) { wantH = need; Fit(need); }
        if (!shown) Native.TrimWorkingSet(); // 氣泡收起後順便縮小工作集
    }

    // ================= 設定 / 尺寸 =================
    public void OnConfig()
    {
        ApplyScale();
        RenderBadge();
        ApplySnapLayout();
        if (Cfg.SnapEdge && drag == null && hwnd != IntPtr.Zero) SnapPosition();
    }

    // ================= 吸附邊緣 / 鏡像 =================
    /// <summary>依吸附狀態翻轉角色，並讓氣泡往螢幕內側靠，不被螢幕邊緣切掉。</summary>
    void ApplySnapLayout()
    {
        string? side = Cfg.SnapEdge ? Cfg.SnapSide : null;
        flip.ScaleX = Cfg.SnapEdge && Cfg.Mirrored ? -1 : 1; // 翻轉會一直維持，直到下次吸到右邊
        if (side == null)
        {
            sideMargin = 0;
            bubbleBody.MaxWidth = 260;
            bubbleBody.MinWidth = 90;
            bubbleHost.HorizontalAlignment = HorizontalAlignment.Center;
            bubbleTail.HorizontalAlignment = HorizontalAlignment.Center;
            bubbleTail.Margin = new Thickness(0, 0, 0, -8);
        }
        else
        {
            double cw = double.IsNaN(charImg.Width) || charImg.Width <= 0 ? 170 : charImg.Width;
            double x0 = Math.Max(0, (W - cw) / 2); // 視窗有 x0 寬度會被推出螢幕外
            sideLeft = side == "left";
            sideMargin = x0 + 6;
            bubbleBody.MaxWidth = Math.Max(130, Math.Min(260, W - x0 - 12));
            bubbleBody.MinWidth = 130;
            bubbleHost.HorizontalAlignment = sideLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            double tailM = Math.Max(0, W / 2 - 9.5 - sideMargin); // 讓箭頭仍指著角色正上方
            bubbleTail.HorizontalAlignment = sideLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            bubbleTail.Margin = sideLeft ? new Thickness(tailM, 0, 0, -8) : new Thickness(0, 0, tailM, -8);
        }
        PlaceBubble();
    }

    /// <summary>角色不透明部分的左右邊緣（實體像素），已考慮翻轉。</summary>
    (double L, double R) CharSpan(int windowLeft, double s)
    {
        var b = BoundsOf(charImg);
        bool fl = flip.ScaleX < 0;
        double ol = fl ? 1 - opaqueR : opaqueL, or = fl ? 1 - opaqueL : opaqueR;
        return (windowLeft + (b.Left + ol * b.Width) * s, windowLeft + (b.Left + or * b.Width) * s);
    }

    static bool HasScreenAt(Forms.Screen self, int x, int y) =>
        Forms.Screen.AllScreens.Any(o => o.DeviceName != self.DeviceName && o.Bounds.Contains(x, y));

    /// <summary>上下不超出螢幕（不改方向）：下緣靠近或掉出去就貼回工作列上方，上緣只在角色頂端超出時拉回。回傳要往下移的實體像素（負 = 往上）。</summary>
    int VerticalShift(Native.RECT r, double s)
    {
        var scr = Forms.Screen.FromHandle(hwnd);
        var wa = scr.WorkingArea;
        int xMid = r.Left + r.Width / 2;
        double zone = Cfg.SnapZone * s;
        if (!HasScreenAt(scr, xMid, wa.Bottom + 1))
        {
            double db = wa.Bottom - r.Bottom; // 視窗底 = 角色腳底（含舞台 4 DIP 下緣）
            if (db <= zone) return (int)Math.Round(db);
        }
        if (!HasScreenAt(scr, xMid, wa.Y - 2))
        {
            var b = BoundsOf(charImg);
            double charTop = r.Top + (b.Top + opaqueT * b.Height) * s;
            if (charTop < wa.Y) return (int)Math.Round(wa.Y - charTop);
        }
        return 0;
    }

    /// <summary>依視窗目前位置判斷該吸哪一邊。已經吸著時要離開更遠才放掉（遲滯），免得在邊界來回閃。</summary>
    string? DetectSide(Native.RECT r, double s)
    {
        var scr = Forms.Screen.FromHandle(hwnd);
        var wa = scr.WorkingArea;
        // 雙螢幕：兩個螢幕交界的那一側不是真正的邊緣，不吸附（不然角色會被卡在交界線上、拉不到另一個螢幕）
        int yMid = r.Bottom - (int)Math.Round(60 * s);
        bool leftOpen = !HasScreenAt(scr, wa.X - 2, yMid), rightOpen = !HasScreenAt(scr, wa.Right + 1, yMid);
        var (l, rr) = CharSpan(r.Left, s);
        double zone = Cfg.SnapZone * s, dl = l - wa.X, dr = wa.Right - rr;
        string? cur = Cfg.SnapSide;
        double zl = cur == "left" ? zone * 1.5 : zone, zr = cur == "right" ? zone * 1.5 : zone;
        bool nearL = leftOpen && dl <= zl, nearR = rightOpen && dr <= zr;
        if (nearL && (!nearR || dl <= dr)) return "left";
        if (nearR) return "right";
        return null;
    }

    /// <summary>拖完放開：離左右邊緣夠近就貼齊並記下是哪一邊。</summary>
    void SnapAfterDrag()
    {
        if (!Cfg.SnapEdge || !Native.GetWindowRect(hwnd, out var r)) return;
        UpdateLayout();
        Cfg.SnapSide = DetectSide(r, Scale);
        if (Cfg.SnapSide == "left") Cfg.Mirrored = true;
        else if (Cfg.SnapSide == "right") Cfg.Mirrored = false;
        ApplySnapLayout();
        SnapPosition();
    }

    /// <summary>把角色貼齊左／右外緣（若有吸附），並確保上下不超出螢幕。</summary>
    void SnapPosition()
    {
        if (!Cfg.SnapEdge || !Native.GetWindowRect(hwnd, out var r)) return;
        UpdateLayout();
        double s = Scale;
        int dx = 0;
        if (Cfg.SnapSide != null)
        {
            var wa = Forms.Screen.FromHandle(hwnd).WorkingArea;
            var (l, rr) = CharSpan(r.Left, s);
            dx = (int)Math.Round(Cfg.SnapSide == "left" ? wa.X - l : wa.Right - rr);
        }
        int dy = VerticalShift(r, s);
        if (dx != 0 || dy != 0) Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left + dx, r.Top + dy, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void ApplyScale()
    {
        double w = Math.Round(170 * Cfg.Scale);
        var src = charImg.Source as BitmapSource;
        double aspect = src != null ? (double)src.PixelHeight / src.PixelWidth : 1;
        charImg.Width = w;
        charImg.Height = Math.Round(w * aspect);
        // 量槽跟角色圖一起縮放，但只在 85%~120% 之間：再小字會看不清，再大會太搶眼
        // （LayoutTransform 會連帶影響版面高度，氣泡位置才會對）
        double bs = Math.Max(0.85, Math.Min(1.2, Cfg.Scale));
        badge.LayoutTransform = new ScaleTransform(bs, bs);
        badge.Margin = new Thickness(0, -16 * bs, 0, 0);
        PlaceBubble();
        if (shown) ScheduleFit();
    }

    void PlaceBubble()
    {
        stage.Measure(new Size(W, double.PositiveInfinity));
        stageH = stage.DesiredSize.Height;
        bubbleHost.Margin = new Thickness(sideLeft ? sideMargin : 0, 0, sideLeft ? 0 : sideMargin, stageH + 6);
    }

    // ================= 氣泡 =================
    public void Say(UIElement content, double seconds = 4)
    {
        bubbleBody.Child = content;
        PlaceBubble();
        ShowBubble();
        hideTimer.Stop();
        if (seconds > 0) { hideTimer.Interval = TimeSpan.FromSeconds(seconds); hideTimer.Start(); }
    }

    public void SayText(string text, double seconds = 4) =>
        Say(Ui.Text(text, 14, Ui.Brown), seconds);

    void ShowBubble()
    {
        if (!shown)
        {
            shown = true;
            bubbleHost.IsHitTestVisible = true;
            bubbleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        ScheduleFit();
    }

    public void HideBubble()
    {
        if (!shown) return;
        shown = false;
        bubbleHost.IsHitTestVisible = false;
        bubbleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        var shrink = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(200));
        bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        ScheduleFit();
    }

    static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ================= 臺詞 / 音效 =================
    public void LoadLines()
    {
        var list = new List<(string, double)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.LinesFile));
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                var text = it.GetProperty("text").GetString() ?? "";
                double w = it.TryGetProperty("weight", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetDouble() : 0;
                list.Add((text, w == 0 ? 1 : w));
            }
        }
        catch { list = new List<(string, double)> { ("臺詞檔案壞掉了...", 1) }; }
        lines = list;
        lastIdx = -1;
    }

    string PickLine()
    {
        if (lines.Count == 0) return "...";
        double total = lines.Sum(l => l.Weight);
        for (int tries = 0; tries < 5; tries++)
        {
            double r = Rng.NextDouble() * total;
            for (int i = 0; i < lines.Count; i++)
            {
                r -= lines[i].Weight;
                if (r <= 0)
                {
                    if (i != lastIdx || lines.Count == 1) { lastIdx = i; return lines[i].Text; }
                    break;
                }
            }
        }
        lastIdx = (lastIdx + 1) % lines.Count;
        return lines[lastIdx].Text;
    }

    // ================= 互動 =================
    void Press(int holdMs)
    {
        SetCharScale(1.07, 0.88, 60, null);
        pressTimer?.Stop();
        pressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(holdMs) };
        pressTimer.Tick += (_, _) =>
        {
            pressTimer!.Stop();
            SetCharScale(1, 1, 120, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1.2 });
        };
        pressTimer.Start();
    }

    void SetCharScale(double sx, double sy, int ms, IEasingFunction? ease)
    {
        charScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        charScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    public void Poke()
    {
        Press(140);
        app.PlaySound(PokeSounds[Rng.Next(PokeSounds.Length)]);
        SayText(PickLine(), 4);
    }

    // 單擊戳一下；雙擊彈出「問 Claude」輸入框（單擊延遲 260 ms，等等看是不是雙擊）
    void OnClick()
    {
        if (clickTimer != null) { clickTimer.Stop(); clickTimer = null; app.OpenQuick(); return; }
        clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        clickTimer.Tick += (_, _) => { clickTimer!.Stop(); clickTimer = null; Poke(); };
        clickTimer.Start();
    }

    // ---- 拖曳：直接讀滑鼠的真實螢幕座標（實體像素），以視窗底部為基準，不會因 DPI 縮放漂移 ----
    void CharDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!Native.GetCursorPos(out var c) || !Native.GetWindowRect(hwnd, out var r)) return;
        charImg.CaptureMouse();
        drag = new Drag { C = c, X = r.Left, Bottom = r.Bottom };
        e.Handled = true;
    }

    void CharMove(object sender, MouseEventArgs e)
    {
        if (drag == null || !Native.GetCursorPos(out var n) || !Native.GetWindowRect(hwnd, out var r)) return;
        if (!drag.Moved)
        {
            if (Math.Sqrt(Math.Pow(n.X - drag.C.X, 2) + Math.Pow(n.Y - drag.C.Y, 2)) / Scale <= 4) return; // 移動超過 4 px 才算拖
            drag.Moved = true;
            charImg.Cursor = Cursors.SizeAll;
        }
        // 寬高沿用視窗當下的實際尺寸；拖動中氣泡收起造成高度變化，底部仍固定，不會跳
        Native.SetWindowPos(hwnd, IntPtr.Zero, drag.X + n.X - drag.C.X, drag.Bottom + n.Y - drag.C.Y - r.Height, r.Width, r.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void EndDrag()
    {
        if (drag == null) return;
        var d = drag;
        drag = null;
        charImg.Cursor = Cursors.Hand;
        if (charImg.IsMouseCaptured) charImg.ReleaseMouseCapture();
        if (d.Moved)
        {
            SnapAfterDrag();
            if (Native.GetWindowRect(hwnd, out var r))
            {
                double s = Scale;
                Cfg.X = Math.Round(r.Left / s);
                Cfg.Y = Math.Round((r.Bottom - Math.Round(H * s)) / s); // 存「未加高時」的視窗頂端
                Cfg.Save();
            }
        }
        else OnClick();
    }

    // ================= 透明區域點穿 =================
    void BuildMask(BitmapSource src)
    {
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pix = new byte[stride * h];
        src.CopyPixels(pix, stride, 0);
        const int step = 4;
        maskW = (w + step - 1) / step;
        maskH = (h + step - 1) / step;
        mask = new bool[maskW * maskH];
        for (int y = 0; y < maskH; y++)
            for (int x = 0; x < maskW; x++)
                mask[y * maskW + x] = pix[((y * step) * w + x * step) * 4 + 3] > 30;
        int minC = maskW, maxC = -1, minR = maskH;
        for (int y = 0; y < maskH; y++)
            for (int x = 0; x < maskW; x++)
                if (mask[y * maskW + x]) { if (x < minC) minC = x; if (x > maxC) maxC = x; if (y < minR) minR = y; }
        if (minR < maskH) opaqueT = (double)minR / maskH;
        if (maxC >= minC) { opaqueL = (double)minC / maskW; opaqueR = (double)(maxC + 1) / maskW; }
    }

    Rect BoundsOf(FrameworkElement el) => el.TransformToVisual(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));

    bool OverChar(double x, double y)
    {
        var r = BoundsOf(charImg);
        if (x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom) return false;
        if (mask == null) return true;
        int px = (int)Math.Floor((x - r.Left) / r.Width * maskW);
        int py = (int)Math.Floor((y - r.Top) / r.Height * maskH);
        if (flip.ScaleX < 0) px = maskW - 1 - px;
        if (px < 0 || py < 0 || px >= maskW || py >= maskH) return false;
        return mask[py * maskW + px];
    }

    bool IsSolid(double x, double y)
    {
        var p = new Point(x, y);
        if (BoundsOf(badge).Contains(p)) return true;
        if (shown && (BoundsOf(bubbleBody).Contains(p) || BoundsOf(bubbleTail).Contains(p))) return true;
        return OverChar(x, y);
    }

    void SetIgnore(bool ignore)
    {
        if (ignore == ignoring || hwnd == IntPtr.Zero) return;
        ignoring = ignore;
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        ex = ignore ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
    }

    // 透明視窗收不到滑鼠訊息，所以輪詢游標位置：游標在視窗外慢慢查，進到視窗內才加快
    bool fastTick;
    void HitTick()
    {
        if (drag != null || hwnd == IntPtr.Zero) return;
        if (!Native.GetCursorPos(out var p) || !Native.GetWindowRect(hwnd, out var r)) return;
        bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        if (inside != fastTick)
        {
            fastTick = inside;
            hitTimer.Interval = TimeSpan.FromMilliseconds(inside ? 33 : 150);
        }
        double s = Scale;
        SetIgnore(!(inside && IsSolid((p.X - r.Left) / s, (p.Y - r.Top) / s)));
    }

    // ================= 金額 / 時間格式 =================
    static string Money(double usd, int? digits = null)
    {
        if (usd > 0 && usd < 0.01) return "<$0.01";
        int d = digits ?? (usd < 1 ? 3 : 2);
        return "$" + usd.ToString("F" + d, CultureInfo.InvariantCulture);
    }

    bool ShowUsedMode => Cfg.ShowUsed;
    string Qtxt(double used) => ShowUsedMode ? $"已用 {Ui.JsRound(used)}%" : $"剩 {Ui.JsRound(100 - used)}%";

    // ================= 量槽 =================
    (TextBlock pct, Border fill) MakeBar(string label, out Grid row, out Border track)
    {
        row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Ui.Text(label, 11, Ui.RowGrey, align: TextAlignment.Left);
        l.TextWrapping = TextWrapping.NoWrap;
        l.TextTrimming = TextTrimming.CharacterEllipsis; // 百分比變長（100% used）時寧可截標題，也不要兩段字疊在一起
        var p = Ui.Text("…", 11, Ui.Ink, bold: true, align: TextAlignment.Right);
        p.TextWrapping = TextWrapping.NoWrap;
        p.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(p, 1);
        row.Children.Add(l);
        row.Children.Add(p);
        var f = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = TrackW, CornerRadius = new CornerRadius(3.5), Background = Ui.Coral };
        track = new Border
        {
            Margin = new Thickness(0, 4, 0, 0), Height = 7, CornerRadius = new CornerRadius(3.5), Background = Ui.TrackBg,
            ClipToBounds = true, Child = f,
        };
        return (p, f);
    }

    void AnimateFill(Border f, double widthPx)
    {
        if (f.Tag is double last && Math.Abs(last - widthPx) < 0.01) return; // 沒變就不要重跑動畫（每次重繪都吃 CPU）
        f.Tag = widthPx;
        f.BeginAnimation(WidthProperty, new DoubleAnimation(widthPx, TimeSpan.FromMilliseconds(600)) { EasingFunction = new QuadraticEase() });
    }

    void SetBar(Border fillEl, TextBlock pctEl, double used)
    {
        used = Math.Max(0, Math.Min(100, used));
        double v = ShowUsedMode ? used : 100 - used;
        pctEl.Text = Ui.JsRound(v) + (ShowUsedMode ? "% used" : "% left");
        AnimateFill(fillEl, TrackW * v / 100);
        fillEl.Background = used >= 90 ? Ui.Red : Ui.Coral;
    }

    public void OnStats(UsageStats s)
    {
        stats = s;
        RenderBadge();
    }

    void RenderBadge()
    {
        if (stats == null) return;
        var rl = stats.Rl;
        var five = rl?.Five;
        fiveBox.Visibility = Cfg.ShowFive ? Visibility.Visible : Visibility.Collapsed;
        fiveBox.Margin = new Thickness(0, 0, 0, Cfg.ShowWeek ? 6 : 0);
        weekBox.Visibility = Cfg.ShowWeek ? Visibility.Visible : Visibility.Collapsed;
        badge.Visibility = Cfg.ShowFive || Cfg.ShowWeek ? Visibility.Visible : Visibility.Collapsed; // 兩條都關就整個量槽不顯示
        if (Cfg.ShowFive)
        {
            if (five != null) SetBar(fill5, pct5, five.Used);
            else { pct5.Text = "—"; AnimateFill(fill5, 0); }
        }
        PlaceBubble(); // 量槽顯示與否會改變舞台高度
        var tips = new List<string>();
        if (Cfg.ShowFive)
            tips.Add(five != null ? $"5 小時：已用 {Ui.JsRound(five.Used)}%" : "5 小時：還沒有官方數字");
        if (!Cfg.ShowWeek)
        {
            var t5 = string.Join("\n", tips);
            if ((badge.ToolTip as string) != t5) badge.ToolTip = t5;
            return;
        }
        if (rl?.Week is not { } wk)
        {
            // 抓不到官方數字就老實說，不估算
            bool off = !Cfg.UseReal;
            pct.Text = off ? "未啟用" : stats.RlTried ? "抓不到" : "讀取中…";
            AnimateFill(fill, 0);
            fill.Background = Ui.Coral;
            tips.Insert(0, off ? "已關閉官方額度讀取（右鍵 → 讀取官方額度 可開啟）"
                : stats.RlErr != null ? "官方額度讀取失敗：" + stats.RlErr + "\n需要電腦上有 claude 命令列且已登入才讀得到"
                : stats.RlTried ? "目前沒有有效的官方數字，點我重新讀取"
                : "正在讀取官方額度…");
            badge.ToolTip = string.Join("\n", tips);
            return;
        }
        SetBar(fill, pct, wk.Used);
        tips.Add($"本週：已用 {Ui.JsRound(wk.Used)}%（官方數字）");
        var tip = string.Join("\n", tips);
        if ((badge.ToolTip as string) != tip) badge.ToolTip = tip;
    }

    // 點量槽：順便重新讀官方額度，並切換賬本氣泡
    void OnBadgeClick()
    {
        app.ProbeNow();
        if (shown && bubbleBody.Child is StackPanel sp && (sp.Tag as string) == "stats") HideBubble();
        else ShowStatsBubble();
    }

    // ================= 賬本 / 每輪結束 =================
    public void ShowStatsBubble()
    {
        if (stats == null) { SayText("還在翻賬本...等我一下", 3); return; }
        var s = stats;
        var panel = new StackPanel { Tag = "stats" };
        panel.Children.Add(Ui.Text("今日（按 API 價折算）", 11, Ui.Grey));
        panel.Children.Add(Ui.Text(Money(s.Today, 2), 20, Ui.Coral, bold: true));
        panel.Children.Add(Ui.Text($"{s.TodayMsgs} 次回覆 · 近 7 天 {Money(s.Week, 2)}", 11, Ui.Grey));
        if (s.Rl?.Five is { } f5 && Cfg.ShowFive)
            panel.Children.Add(Ui.Text($"5 小時 {Qtxt(f5.Used)}", 11, Ui.Grey));
        if (Cfg.ShowWeek)
        {
            if (s.Rl?.Week is { } wk)
                panel.Children.Add(Ui.Text($"本週 {Qtxt(wk.Used)}", 11, Ui.Grey));
            else
                panel.Children.Add(Ui.Text("本週額度：抓不到官方數字", 11, Ui.Grey));
        }

        // 近 7 天長條圖（今日用珊瑚色）
        double max = Math.Max(s.Days.Select(d => d.Value).DefaultIfEmpty(0).Max(), 0.0001);
        string todayKey = s.Days[^1].Key;
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var barsWrap = new Grid { Height = 46, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2) };
        foreach (var (d, v) in s.Days)
        {
            var bar = new Grid { Width = 22, Height = Math.Max(2, v / max * 46), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0) };
            bar.Children.Add(new Border
            {
                Background = d == todayKey ? Ui.Coral : Ui.BarBg,
                CornerRadius = new CornerRadius(3, 3, 0, 0),
            });
            var lbl = Ui.Text(d.Substring(8), 9, Ui.Grey);
            lbl.TextWrapping = TextWrapping.NoWrap;
            lbl.VerticalAlignment = VerticalAlignment.Bottom;
            lbl.Margin = new Thickness(0, 0, 0, -14);
            lbl.LineHeight = 12;
            bar.Children.Add(lbl);
            bars.Children.Add(bar);
        }
        barsWrap.Children.Add(bars);
        panel.Children.Add(barsWrap);
        panel.Children.Add(new Border { Height = 14 }); // 日期標籤的位置

        if (s.ByModel.Count > 0)
        {
            var tbl = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            foreach (var (m, v) in s.ByModel.OrderByDescending(x => x.Value))
            {
                tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = Ui.Text(m.Replace("claude-", ""), 12, Ui.Ink, align: TextAlignment.Left);
                name.Margin = new Thickness(4, 1, 4, 1); name.TextWrapping = TextWrapping.NoWrap;
                var cost = Ui.Text(Money(v, 2), 12, Ui.Ink, align: TextAlignment.Right);
                cost.Margin = new Thickness(4, 1, 4, 1); cost.TextWrapping = TextWrapping.NoWrap;
                Grid.SetRow(name, row); Grid.SetColumn(name, 0);
                Grid.SetRow(cost, row); Grid.SetColumn(cost, 1);
                tbl.Children.Add(name); tbl.Children.Add(cost);
                row++;
            }
            panel.Children.Add(tbl);
        }
        Say(panel, 10);
    }

    static string TurnComment(double usd) =>
        usd < 0.05 ? "這輪很省～" :
        usd < 0.5 ? "嗯，正常發揮。" :
        usd < 2 ? "token 燒得也太快了..." :
        usd < 5 ? "這輪有點貴...我 thinking 了好久..." :
        "user，該交錢了....";

    public void OnTurn(TurnInfo t)
    {
        app.PlaySound("task-end-a.wav");
        if (!Cfg.TurnBubble) return;
        // 正在提醒你回覆／批准：不蓋掉，把這輪花費附在提醒氣泡最下面
        if (shown && bubbleBody.Child is StackPanel attn && (attn.Tag as string) == "attention")
        {
            foreach (var old in attn.Children.OfType<FrameworkElement>().Where(e => (e.Tag as string) == "turnCost").ToList()) attn.Children.Remove(old);
            var cost = Ui.Text("這輪 " + Money(t.Cost), 11, Ui.Grey);
            cost.Tag = "turnCost";
            attn.Children.Add(cost);
            Say(attn, 0);
            return;
        }
        string proj = string.IsNullOrEmpty(t.Cwd) ? "" : t.Cwd.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text((proj != "" ? proj + " · " : "") + "這輪做完啦", 11, Ui.Grey));
        panel.Children.Add(Ui.Text(Money(t.Cost), 20, Ui.Coral, bold: true));
        panel.Children.Add(Ui.Text(TurnComment(t.Cost), 14, Ui.Brown));
        Say(panel, Cfg.TurnSeconds);
        Press(160);
    }

    /// <summary>Claude 在等你批准／回覆：發聲加一個不會自己消失的氣泡，點一下或處理完才收掉。</summary>
    public void OnAttention(string type, string message, string cwd)
    {
        app.PlaySound("Ya1.mp3");
        if (!Cfg.AttentionBubble) return;
        string proj = string.IsNullOrEmpty(cwd) ? "" : cwd.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        string head = type switch
        {
            "permission_prompt" => "可以幫忙點個頭嗎？",
            "idle_prompt" => "在等你回覆喔～",
            "elicitation_dialog" => "有件事想問問你～",
            _ => "這裡需要你看一下喔～",
        };
        var panel = new StackPanel { Tag = "attention" };
        panel.Children.Add(Ui.Text((proj != "" ? proj + " · " : "") + "Claude 在等你", 11, Ui.Grey));
        panel.Children.Add(Ui.Text(head, 18, Ui.Coral, bold: true));
        if (!string.IsNullOrWhiteSpace(message))
            panel.Children.Add(Ui.Text(message.Length > 80 ? message.Substring(0, 80) + "…" : message, 12, Ui.Brown));
        Say(panel, 0);
        Press(160);
    }

    /// <summary>今日花費超過每日預算（PetApp 已確認有啟用、有設金額、今天還沒提醒過）。</summary>
    public void OnBudget(double today, double budget)
    {
        app.PlaySound("Ya2.mp3");
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("今天的預算到囉", 11, Ui.Grey));
        panel.Children.Add(Ui.Text(Money(today) + " / " + Money(budget, 0), 20, Ui.Coral, bold: true));
        panel.Children.Add(Ui.Text("只是輕輕提醒你一下喔～", 14, Ui.Brown));
        Say(panel, 12);
        Press(160);
    }

    /// <summary>待處理都處理完了：只收掉提醒氣泡，不動別的氣泡。</summary>
    public void ClearAttention()
    {
        if (bubbleBody.Child is StackPanel sp && (sp.Tag as string) == "attention") HideBubble();
    }

    public void Bye() =>
        SayText(new[] { "下班啦～拜拜", "Claude Code 關了，我也溜了～", "今天辛苦啦，記得早點睡" }[Rng.Next(3)], 0);

    public void Greet() => SayText("小克上班啦～", 3);
}
