using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace XiaoKe;

/// <summary>雙擊小克彈出的快速輸入框：仿 Claude 快捷輸入框，Enter 交給 Claude 桌面版開新對話。</summary>
sealed class QuickWindow : Window
{
    const int QW = 640, QH = 96;
    readonly PetApp app;
    readonly TextBox input;
    readonly int xPhys, yPhys;
    readonly double scale;
    readonly long shownAt = Environment.TickCount64;
    bool closing;

    public QuickWindow(PetApp app, int xPhys, int yPhys, double scale)
    {
        this.app = app;
        this.xPhys = xPhys; this.yPhys = yPhys; this.scale = scale;
        Title = "問 Claude";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = QW;
        Height = QH;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = xPhys / scale; Top = yPhys / scale;

        // 珊瑚色星芒圖示
        var burst = new GeometryGroup();
        void L(double x1, double y1, double x2, double y2) => burst.Children.Add(new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
        L(12, 2.5, 12, 9); L(12, 15, 12, 21.5); L(2.5, 12, 9, 12); L(15, 12, 21.5, 12);
        L(5.3, 5.3, 9.6, 9.6); L(14.4, 14.4, 18.7, 18.7); L(18.7, 5.3, 14.4, 9.6); L(9.6, 14.4, 5.3, 18.7);
        L(8.4, 3.2, 10.4, 8.6); L(15.6, 20.8, 13.6, 15.4); L(20.8, 8.4, 15.4, 10.4); L(3.2, 15.6, 8.6, 13.6);
        var icon = new Viewbox
        {
            Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24, Height = 24,
                Children = { new Path { Data = burst, Stroke = Ui.Coral, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round } },
            },
        };

        input = new TextBox
        {
            FontSize = 17, FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Microsoft YaHei UI"), Foreground = Ui.Ink,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            SpellCheck = { IsEnabled = false }, Template = Ui.PlainTextBox(),
        };
        var placeholder = new TextBlock
        {
            Text = "What can I help you with today?", FontSize = 17, Foreground = Ui.Br("#6f6e77"),
            FontFamily = input.FontFamily, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var inputHost = new Grid { Children = { input, placeholder } };

        var grid = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0); Grid.SetColumn(inputHost, 2);
        grid.Children.Add(icon); grid.Children.Add(inputHost);

        var box = new Border
        {
            Margin = new Thickness(12, 14, 12, 0), Height = 58, VerticalAlignment = VerticalAlignment.Top,
            Background = Ui.Br("#fbfbfd"), BorderBrush = Ui.Br("#dcdce3"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Effect = Ui.Shadow(22, 6, 0.16), Child = grid,
        };
        Content = box;

        box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        // Enter（非輸入法組字中）送出；組字中的 Enter 會變成 Key.ImeProcessed
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseOnce(); }
        };
        // 點到別處就收起；剛彈出時 Windows 可能搶不到焦點，前 0.6 秒的失焦忽略
        Deactivated += (_, _) => { if (Environment.TickCount64 - shownAt > 600) CloseOnce(); };
        Loaded += (_, _) => { Activate(); input.Focus(); Keyboard.Focus(input); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(h, IntPtr.Zero, xPhys, yPhys, (int)Math.Round(QW * scale), (int)Math.Round(QH * scale), Native.SWP_NOZORDER);
    }

    void Submit()
    {
        var text = input.Text;
        CloseOnce();
        app.SubmitQuick(text);
    }

    void CloseOnce()
    {
        if (closing) return;
        closing = true;
        Close();
    }
}
