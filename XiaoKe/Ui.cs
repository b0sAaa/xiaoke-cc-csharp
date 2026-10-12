using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace XiaoKe;

static class Ui
{
    public const string NB = " "; // 不換行空格：避免「Resets 時間」被拆成兩行

    public static SolidColorBrush Br(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Coral = Br("#d97757"), Red = Br("#c64545"), Ink = Br("#2b2925"), Sand = Br("#faf9f5"),
        Brown = Br("#8a5a44"), Grey = Br("#a09d96"), TrackBg = Br("#eee5da"), BadgeBorder = Br("#e6dccf"), RowGrey = Br("#5f5b54"),
        BarBg = Br("#e8e0d2");

    public static readonly FontFamily Font = new("Microsoft YaHei UI, Microsoft JhengHei UI, Segoe UI");

    public static DropShadowEffect Shadow(double blur, double depth, double opacity, string color = "#141413")
    {
        var e = new DropShadowEffect
        {
            BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity,
            Color = (Color)ColorConverter.ConvertFromString(color),
        };
        e.Freeze();
        return e;
    }

    /// <summary>JS 的 Math.round：.5 往上進位（C# 預設是銀行家捨入）。</summary>
    public static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    public static TextBlock Text(string s, double size, Brush fg, bool bold = false, TextAlignment align = TextAlignment.Center)
    {
        var t = new TextBlock
        {
            Text = s, FontSize = size, Foreground = fg, FontFamily = Font,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, TextAlignment = align,
            LineHeight = size * 1.45, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        return t;
    }

    /// <summary>把視窗內容存成 PNG（疊在淺灰底上方便肉眼檢查透明區域）。</summary>
    public static void Capture(Window w, string file)
    {
        w.UpdateLayout();
        double s = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int pw = (int)Math.Round(w.ActualWidth * s), ph = (int)Math.Round(w.ActualHeight * s);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    /// <summary>沒有聚焦 / 滑過特效的 TextBox 外觀（預設樣板聚焦時會把邊框變成系統藍色）。</summary>
    public static ControlTemplate PlainTextBox(double cornerRadius = 0)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        border.AppendChild(host);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = border };
    }
}
