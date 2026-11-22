using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KeyTrace.Interop;

namespace KeyTrace.Ui;

/// <summary>
/// KeyTrace 极简桌面主窗口：仅展示今日实时击键总数与今日分时段（00–23 时）击键柱状图。
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly SolidColorBrush ActiveHourBrush = CreateFrozenBrush(0x5E, 0xE0, 0x8A);

    private readonly string _eventsDir;
    private readonly LiveTelemetry _telemetry;
    private readonly bool _isViewerMode;

    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly Button _actionBtn;
    private readonly TextBlock _dateText;
    private readonly TextBlock _countValueText;
    private readonly TextBlock _peakText;
    private readonly Grid _barsPlotGrid;
    private readonly Grid[] _barSlots = new Grid[24];
    private readonly Border[] _bars = new Border[24];

    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _diskTimer;

    private DateOnly _diskDay = DateOnly.FromDateTime(DateTime.Now);
    private long _diskBaseTotal;
    private readonly int[] _diskHourly = new int[24];

    private bool _paused;
    private bool _isLoading;
    private long _lastRenderedTotal = -1;
    private int _lastRenderedHour = -1;
    private readonly int[] _lastRenderedHourly = new int[24];

    public MainWindow(
        string eventsDir,
        LiveTelemetry telemetry,
        bool isViewerMode,
        bool isPausedInitial)
    {
        _eventsDir = eventsDir;
        _telemetry = telemetry;
        _isViewerMode = isViewerMode;
        _paused = isPausedInitial;

        Title = isViewerMode ? "KeyTrace (只读)" : "KeyTrace";
        Width = 480;
        Height = 368;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (AppIcons.LoadWindowIcon() is ImageSource appIcon)
        {
            Icon = appIcon;
        }

        DarkTheme.ApplyDarkTitleBar(this);
        DarkTheme.ApplyWindowResources(this);

        _statusDot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        _statusText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };

        _actionBtn = DarkTheme.CreateButton(isViewerMode ? "升级后台" : "暂停", primary: isViewerMode, compact: true);
        _actionBtn.Padding = new Thickness(10, 4, 10, 4);
        if (isViewerMode)
        {
            _actionBtn.ToolTip = "通过 UAC 升级后台托盘服务";
        }

        _actionBtn.Click += (_, _) =>
        {
            if (_isViewerMode)
            {
                RunInstallTaskScript();
            }
            else
            {
                TogglePauseRequested?.Invoke();
            }
        };

        _dateText = new TextBlock
        {
            Text = _diskDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FontFamily = DarkTheme.MonoFont,
            FontSize = 11.5,
            Foreground = DarkTheme.FaintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _countValueText = new TextBlock
        {
            Text = "0",
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            Foreground = DarkTheme.TextBrush,
            Margin = new Thickness(0, 2, 0, 0),
        };

        _peakText = new TextBlock
        {
            Text = "今日暂无击键",
            FontSize = 11.5,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _barsPlotGrid = new Grid { MinHeight = 84 };
        _barsPlotGrid.SizeChanged += (_, _) => RefreshDisplay(forceChart: true);

        Content = BuildLayout();
        SetPaused(isPausedInitial);

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _liveTimer.Tick += (_, _) => OnLiveTimerTick();

        _diskTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _diskTimer.Tick += async (_, _) => await ReloadCountsAsync();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _ = ReloadCountsAsync();
                _liveTimer.Start();
                _diskTimer.Start();
            }
            else
            {
                _liveTimer.Stop();
                _diskTimer.Stop();
            }
        };

        Closing += (_, e) =>
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public bool AllowClose { get; set; }

    public event Action? TogglePauseRequested;

    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ForceForeground();

        // Win11 托盘折叠面板在鼠标释放后约 50ms 异步收起，容易夺走前景焦点；
        // 等消息队列空闲后再补一次激活，确保窗口稳定置顶。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (IsVisible && !IsActive)
            {
                ForceForeground();
            }
        });
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_isViewerMode)
        {
            _statusDot.Fill = DarkTheme.BlueBrush;
            _statusText.Text = "只读";
            _statusText.Foreground = DarkTheme.BlueBrush;
            _actionBtn.Content = "升级后台";
        }
        else
        {
            _statusDot.Fill = paused ? DarkTheme.FaintBrush : DarkTheme.GreenBrush;
            _statusText.Text = paused ? "已暂停" : "录制中";
            _statusText.Foreground = paused ? DarkTheme.DimBrush : DarkTheme.GreenBrush;
            _actionBtn.Content = paused ? "继续" : "暂停";
        }
    }

    public async Task ReloadCountsAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        try
        {
            TodayCountsSnapshot snap = await DesktopStatsService.LoadTodayCountsAsync(_eventsDir);
            ApplyCountsSnapshot(snap);
        }
        catch (Exception)
        {
            // 后台读取异常时保留已有计数
        }
        finally
        {
            _isLoading = false;
        }
    }

    internal void ApplyCountsSnapshot(TodayCountsSnapshot snap)
    {
        _diskDay = snap.Day;
        _diskBaseTotal = snap.TotalKeystrokes;
        Array.Copy(snap.HourlyKeystrokes, _diskHourly, 24);
        _telemetry.SeedFromTodayCount(snap.TotalKeystrokes, snap.MaxEventTs);
        RefreshDisplay(forceChart: true);
    }

    private Border BuildLayout()
    {
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Row 0: 顶部精简状态栏
        DockPanel header = new() { LastChildFill = false };
        StackPanel leftBrand = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (AppIcons.LoadWindowIcon() is ImageSource brandIcon)
        {
            Image logoImg = new()
            {
                Source = brandIcon,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(logoImg, BitmapScalingMode.HighQuality);
            leftBrand.Children.Add(logoImg);
        }

        leftBrand.Children.Add(new TextBlock
        {
            Text = "KeyTrace",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        StackPanel statusPill = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusPill.Children.Add(_statusDot);
        statusPill.Children.Add(_statusText);
        leftBrand.Children.Add(statusPill);
        header.Children.Add(leftBrand);

        DockPanel.SetDock(_actionBtn, Dock.Right);
        header.Children.Add(_actionBtn);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Row 1: 今日总计数卡片
        StackPanel countCardContent = new();
        Grid countTopRow = BuildSplitRow(
            new TextBlock
            {
                Text = "今日击键",
                FontSize = 12.5,
                Foreground = DarkTheme.DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
            _dateText);
        countCardContent.Children.Add(countTopRow);

        StackPanel numberRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        numberRow.Children.Add(_countValueText);
        numberRow.Children.Add(new TextBlock
        {
            Text = "次",
            FontSize = 14,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(7, 0, 0, 7),
        });
        countCardContent.Children.Add(numberRow);

        Border countCard = DarkTheme.CreateCard(countCardContent, new Thickness(18, 12, 18, 14));
        countCard.Margin = new Thickness(0, 12, 0, 12);
        Grid.SetRow(countCard, 1);
        root.Children.Add(countCard);

        // Row 2: 24 小时分时段柱状图卡片
        Grid chartCardContent = new();
        chartCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chartCardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chartCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid chartHeader = BuildSplitRow(
            new TextBlock
            {
                Text = "分时段击键 (00 - 23 时)",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = DarkTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
            _peakText);
        chartHeader.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(chartHeader, 0);
        chartCardContent.Children.Add(chartHeader);

        Grid axisGrid = new() { Margin = new Thickness(0, 5, 0, 0) };
        for (int h = 0; h < 24; h++)
        {
            _barsPlotGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            axisGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border bar = new()
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = DarkTheme.ElevatedBrush,
            };

            Grid slot = new()
            {
                Background = Brushes.Transparent,
                ToolTip = $"{h:00}:00 - {h:00}:59\n0 次",
            };
            slot.Children.Add(bar);
            Grid.SetColumn(slot, h);
            _barsPlotGrid.Children.Add(slot);

            _barSlots[h] = slot;
            _bars[h] = bar;

            if (h % 3 == 0)
            {
                TextBlock tick = new()
                {
                    Text = h.ToString("00", CultureInfo.InvariantCulture),
                    FontFamily = DarkTheme.MonoFont,
                    FontSize = 10.5,
                    Foreground = DarkTheme.FaintBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                Grid.SetColumn(tick, h);
                axisGrid.Children.Add(tick);
            }
        }

        Grid.SetRow(_barsPlotGrid, 1);
        chartCardContent.Children.Add(_barsPlotGrid);
        Grid.SetRow(axisGrid, 2);
        chartCardContent.Children.Add(axisGrid);

        Border chartCard = DarkTheme.CreateCard(chartCardContent, new Thickness(16, 12, 16, 12));
        Grid.SetRow(chartCard, 2);
        root.Children.Add(chartCard);

        return new Border
        {
            Background = DarkTheme.BgBrush,
            Padding = new Thickness(20, 16, 20, 18),
            Child = root,
        };
    }

    private void OnLiveTimerTick()
    {
        if (_isViewerMode)
        {
            _telemetry.PollTodayFile(_eventsDir);
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _diskDay)
        {
            _diskDay = today;
            _diskBaseTotal = 0;
            Array.Clear(_diskHourly, 0, 24);
            _ = ReloadCountsAsync();
            RefreshDisplay(forceChart: true);
            return;
        }

        RefreshDisplay(forceChart: false);
    }

    private void RefreshDisplay(bool forceChart)
    {
        LiveTelemetrySnapshot live = _telemetry.Snapshot();
        long total = Math.Max(_diskBaseTotal, live.TodayKeystrokes);
        if (forceChart || total != _lastRenderedTotal)
        {
            _countValueText.Text = total.ToString("N0", CultureInfo.InvariantCulture);
            _lastRenderedTotal = total;
        }

        string todayText = _diskDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_dateText.Text != todayText)
        {
            _dateText.Text = todayText;
        }

        int currentHour = DateTime.Now.Hour;
        int[] merged = new int[24];
        bool chartChanged = forceChart || currentHour != _lastRenderedHour;
        for (int h = 0; h < 24; h++)
        {
            int delta = h < live.LiveHourlyDelta.Count ? live.LiveHourlyDelta[h] : 0;
            merged[h] = _diskHourly[h] + delta;
            if (merged[h] != _lastRenderedHourly[h])
            {
                chartChanged = true;
            }
        }

        if (!chartChanged)
        {
            return;
        }

        Array.Copy(merged, _lastRenderedHourly, 24);
        _lastRenderedHour = currentHour;

        int maxVal = 0;
        int peakHour = 0;
        for (int h = 0; h < 24; h++)
        {
            if (merged[h] > maxVal)
            {
                maxVal = merged[h];
                peakHour = h;
            }
        }

        _peakText.Text = maxVal > 0
            ? string.Create(CultureInfo.InvariantCulture, $"峰值 {peakHour:00} 时 · {maxVal:N0} 次")
            : "今日暂无击键";

        double plotHeight = _barsPlotGrid.ActualHeight > 24 ? _barsPlotGrid.ActualHeight - 4 : 84;
        for (int h = 0; h < 24; h++)
        {
            int v = merged[h];
            _bars[h].Height = v == 0 || maxVal == 0
                ? 2
                : Math.Max(4.0, (double)v / maxVal * plotHeight);
            _bars[h].Background = v == 0
                ? DarkTheme.ElevatedBrush
                : (h == currentHour ? ActiveHourBrush : DarkTheme.GreenBrush);
            _barSlots[h].ToolTip = string.Create(CultureInfo.InvariantCulture, $"{h:00}:00 - {h:00}:59\n{v:N0} 次");
        }
    }

    private void ForceForeground()
    {
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.BringWindowToTop(hwnd);
            _ = NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    private void RunInstallTaskScript()
    {
        string[] candidates =
        [
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "install-task.bat"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "keytrace", "scripts", "install-task.bat"),
        ];

        string? script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            _statusText.Text = "未找到安装脚本";
            _statusText.Foreground = DarkTheme.RedBrush;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                Verb = "runas",
                UseShellExecute = true,
            });
            _statusText.Text = "已调起升级";
            _statusText.Foreground = DarkTheme.GreenBrush;
        }
        catch (Exception)
        {
            _statusText.Text = "已取消升级";
            _statusText.Foreground = DarkTheme.DimBrush;
        }
    }

    private static Grid BuildSplitRow(UIElement left, UIElement right)
    {
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
