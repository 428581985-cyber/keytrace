using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using KeyTrace.Interop;

namespace KeyTrace.Ui;

/// <summary>
/// 桌面可选悬浮迷你状态条：始终置顶但不抢任何前台软件的输入焦点（WS_EX_NOACTIVATE），
/// 显示当前录制状态、今日实时击键数、近 30 秒速度与最近 8 秒内的实时纠错提醒。
/// </summary>
internal sealed class MiniHudWindow : Window
{
    private const double HudWidth = 276;
    private const double HudHeight = 36;

    private readonly Ellipse _statusDot;
    private readonly TextBlock _keysText;
    private readonly TextBlock _wpmText;
    private readonly Border _correctionBadge;
    private readonly TextBlock _correctionText;
    private readonly MenuItem _pauseMenuItem;

    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStartDip;
    private Point _windowStartDip;

    public MiniHudWindow(double? savedLeft, double? savedTop, bool canTogglePause)
    {
        Width = HudWidth;
        Height = HudHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "KeyTrace HUD";

        _statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = DarkTheme.GreenBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _keysText = new TextBlock
        {
            Text = "今日 0 键",
            FontFamily = DarkTheme.UiFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _wpmText = new TextBlock
        {
            Text = "待机",
            FontFamily = DarkTheme.MonoFont,
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        _correctionText = new TextBlock
        {
            Text = "",
            FontFamily = DarkTheme.MonoFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.AmberBrush,
        };

        _correctionBadge = new Border
        {
            Background = DarkTheme.AmberBgBrush,
            BorderBrush = DarkTheme.AmberBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _correctionText,
        };

        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_statusDot, 0);
        Grid.SetColumn(_keysText, 1);
        Grid.SetColumn(_wpmText, 2);
        Grid.SetColumn(_correctionBadge, 3);

        row.Children.Add(_statusDot);
        row.Children.Add(_keysText);
        row.Children.Add(_wpmText);
        row.Children.Add(_correctionBadge);

        SolidColorBrush pillBg = new(Color.FromArgb(0xE8, 0x1F, 0x1F, 0x1F));
        pillBg.Freeze();

        Border shell = new()
        {
            Background = pillBg,
            BorderBrush = DarkTheme.BorderStrongBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black,
            },
            Child = row,
        };

        Content = shell;

        _pauseMenuItem = new MenuItem
        {
            Header = "暂停录制",
            IsEnabled = canTogglePause,
        };
        _pauseMenuItem.Click += (_, _) => TogglePauseRequested?.Invoke();

        MenuItem openItem = new() { Header = "打开主窗口" };
        openItem.Click += (_, _) => OpenMainWindowRequested?.Invoke();

        MenuItem hideItem = new() { Header = "隐藏悬浮条" };
        hideItem.Click += (_, _) => HideRequested?.Invoke();

        ContextMenu menu = new()
        {
            Background = DarkTheme.ElevatedBrush,
            Foreground = DarkTheme.TextBrush,
            BorderBrush = DarkTheme.BorderStrongBrush,
        };
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(hideItem);
        ContextMenu = menu;

        ApplyInitialPosition(savedLeft, savedTop);

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    public event Action? OpenMainWindowRequested;
    public event Action? TogglePauseRequested;
    public event Action? HideRequested;
    public event Action<double, double>? PositionChanged;

    public void UpdateState(LiveTelemetrySnapshot live, bool paused)
    {
        _statusDot.Fill = paused ? DarkTheme.FaintBrush : DarkTheme.GreenBrush;
        _pauseMenuItem.Header = paused ? "继续录制" : "暂停录制";
        _keysText.Text = string.Create(CultureInfo.InvariantCulture, $"今日 {live.TodayKeystrokes:N0} 键");

        if (paused)
        {
            _wpmText.Text = "已暂停";
            _wpmText.Foreground = DarkTheme.FaintBrush;
        }
        else if (live.LiveWpm > 0)
        {
            _wpmText.Text = string.Create(CultureInfo.InvariantCulture, $"{live.LiveWpm} wpm");
            _wpmText.Foreground = DarkTheme.GreenBrush;
        }
        else
        {
            _wpmText.Text = "待机";
            _wpmText.Foreground = DarkTheme.DimBrush;
        }

        if (!string.IsNullOrEmpty(live.LastCorrectionCompact))
        {
            _correctionText.Text = live.LastCorrectionCompact;
            _correctionBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _correctionBadge.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void ApplyInitialPosition(double? savedLeft, double? savedTop)
    {
        Rect work = SystemParameters.WorkArea;
        double left = savedLeft ?? (work.Right - HudWidth - 20);
        double top = savedTop ?? (work.Bottom - HudHeight - 20);
        (Left, Top) = ClampToVirtualScreen(left, top);
    }

    private static (double Left, double Top) ClampToVirtualScreen(double left, double top)
    {
        double minX = SystemParameters.VirtualScreenLeft;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - HudWidth;
        double minY = SystemParameters.VirtualScreenTop;
        double maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - HudHeight;

        return (
            Math.Clamp(left, minX, Math.Max(minX, maxX)),
            Math.Clamp(top, minY, Math.Max(minY, maxY)));
    }

    private Point CursorScreenDip(MouseEventArgs e)
    {
        Point px = PointToScreen(e.GetPosition(this));
        Matrix transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return transform.Transform(px);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragMoved = false;
        _dragStartDip = CursorScreenDip(e);
        _windowStartDip = new Point(Left, Top);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Point current = CursorScreenDip(e);
        double dx = current.X - _dragStartDip.X;
        double dy = current.Y - _dragStartDip.Y;
        if (Math.Abs(dx) >= 4 || Math.Abs(dy) >= 4)
        {
            _dragMoved = true;
            (double clampedLeft, double clampedTop) = ClampToVirtualScreen(_windowStartDip.X + dx, _windowStartDip.Y + dy);
            Left = clampedLeft;
            Top = clampedTop;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        if (!_dragMoved)
        {
            OpenMainWindowRequested?.Invoke();
        }
        else
        {
            PositionChanged?.Invoke(Left, Top);
        }

        e.Handled = true;
    }
}
