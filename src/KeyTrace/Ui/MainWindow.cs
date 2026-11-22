using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KeyTrace.Analysis;
using KeyTrace.Collect;
using KeyTrace.Interop;
using WpfControl = System.Windows.Controls.Control;

namespace KeyTrace.Ui;

/// <summary>
/// KeyTrace 独立原生暗色桌面主窗口：精简三栏式（实时监控 / 弱键与分布 / 设置）。
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly string _eventsDir;
    private readonly string _baseDir;
    private readonly UserConfig _config;
    private readonly LiveTelemetry _telemetry;
    private readonly bool _isViewerMode;

    private readonly Button[] _navButtons = new Button[3];
    private readonly Border[] _navAccents = new Border[3];
    private readonly UIElement[] _tabRoots = new UIElement[3];

    private readonly TextBlock _pageTitleText;
    private readonly StackPanel _rangeToolbarPanel;
    private readonly Button _todayRangeBtn;
    private readonly Button _days7RangeBtn;

    private readonly Ellipse _sideStatusDot;
    private readonly TextBlock _sideStatusText;
    private readonly Button _primaryBottomBtn;

    // Tab 0: 实时监控
    private readonly TextBlock _kpiKeysValue;
    private readonly TextBlock _kpiKeysSub;
    private readonly TextBlock _kpiWpmValue;
    private readonly TextBlock _kpiWpmSub;
    private readonly TextBlock _kpiRightLabel;
    private readonly TextBlock _kpiRightValue;
    private readonly TextBlock _kpiRightSub;
    private readonly Border _guardBanner;
    private readonly TextBlock _guardText;
    private readonly TextBlock _chartTitleText;
    private readonly TextBlock _chartMetaText;
    private readonly Grid _chartGrid;
    private readonly CheckBox _hideLiveCheckOverview;
    private readonly StackPanel _liveFeedPanel;

    // Tab 1: 弱键与分布
    private readonly CheckBox _showSpLabelsCheck;
    private readonly KeyboardHeatmapControl _heatmap;
    private readonly StackPanel _weakListPanel;
    private readonly StackPanel _appsListPanel;

    // Tab 2: 设置
    private readonly CheckBox _miniHudCheck;
    private readonly CheckBox _hideLiveCheckSettings;
    private readonly CheckBox _captureTextCheck;
    private readonly TextBox _exclusionInput;
    private readonly WrapPanel _customExclusionsPanel;
    private readonly TextBlock _settingsFeedbackText;

    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _statsTimer;

    private StatsRange _range = StatsRange.Today;
    private bool _paused;
    private bool _suppressSettingEvents;
    private bool _isLoadingStats;
    private DesktopSnapshot? _snapshot;

    public MainWindow(
        string eventsDir,
        string baseDir,
        UserConfig config,
        LiveTelemetry telemetry,
        bool isViewerMode,
        bool isPausedInitial)
    {
        _eventsDir = eventsDir;
        _baseDir = baseDir;
        _config = config;
        _telemetry = telemetry;
        _isViewerMode = isViewerMode;
        _paused = isPausedInitial;

        Title = isViewerMode ? "KeyTrace · 键盘监控 (只读视图)" : "KeyTrace · 键盘监控";
        Width = 920;
        Height = 640;
        MinWidth = 780;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        DarkTheme.ApplyDarkTitleBar(this);
        DarkTheme.ApplyWindowResources(this);

        Grid shell = new() { Background = DarkTheme.BgBrush };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---------- 左侧导航栏 ----------
        _sideStatusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        _sideStatusText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
        _primaryBottomBtn = isViewerMode
            ? DarkTheme.CreateButton("升级后台托盘服务", primary: true, compact: true)
            : DarkTheme.CreateButton("暂停录制", compact: true);
        _primaryBottomBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _primaryBottomBtn.Margin = new Thickness(0, 0, 0, 8);
        _primaryBottomBtn.Click += (_, _) =>
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

        Border sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        shell.Children.Add(sidebar);

        // ---------- 右侧主区域 ----------
        Grid mainArea = new();
        mainArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _pageTitleText = new TextBlock
        {
            Text = "实时监控",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _todayRangeBtn = DarkTheme.CreatePillButton("今日", true);
        _days7RangeBtn = DarkTheme.CreatePillButton("近 7 天", false);
        _rangeToolbarPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Border topBar = BuildTopBar();
        Grid.SetRow(topBar, 0);
        mainArea.Children.Add(topBar);

        // Tab 0: 实时监控
        _kpiKeysValue = KpiValueText("0");
        _kpiKeysSub = KpiSubText("正在读取...");
        _kpiWpmValue = KpiValueText("0 wpm");
        _kpiWpmSub = KpiSubText("实时 30s：待机");
        _kpiRightLabel = new TextBlock { Text = "准确率", FontSize = 12, Foreground = DarkTheme.DimBrush };
        _kpiRightValue = KpiValueText("100.0%");
        _kpiRightSub = KpiSubText("确认纠错 0 次");
        _guardText = new TextBlock { FontSize = 12.5, Foreground = DarkTheme.AmberBrush, TextWrapping = TextWrapping.Wrap };
        _guardBanner = new Border
        {
            Background = DarkTheme.AmberBgBrush,
            BorderBrush = DarkTheme.AmberBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 14),
            Visibility = Visibility.Collapsed,
            Child = _guardText,
        };
        _chartTitleText = new TextBlock { Text = "今日分时段击键 (24 小时)", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = DarkTheme.TextBrush };
        _chartMetaText = new TextBlock { Text = "", FontSize = 12, Foreground = DarkTheme.DimBrush, VerticalAlignment = VerticalAlignment.Center };
        _chartGrid = new Grid { Height = 132, Margin = new Thickness(0, 10, 0, 0) };
        _hideLiveCheckOverview = new CheckBox { Content = "隐藏明文", IsChecked = _config.HideLiveText };
        _liveFeedPanel = new StackPanel();
        _tabRoots[0] = BuildMonitorTab();

        // Tab 1: 弱键与分布
        _showSpLabelsCheck = new CheckBox { Content = "显示双拼键帽", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        _heatmap = new KeyboardHeatmapControl();
        _weakListPanel = new StackPanel();
        _appsListPanel = new StackPanel();
        _tabRoots[1] = BuildWeakAndAppsTab();

        // Tab 2: 设置
        _miniHudCheck = new CheckBox { Content = "显示桌面悬浮状态条 (Mini HUD，可拖动、不抢焦点)", IsChecked = _config.ShowMiniHud };
        _hideLiveCheckSettings = new CheckBox { Content = "隐私模式：隐藏实时记录里的明文", IsChecked = _config.HideLiveText, Margin = new Thickness(0, 10, 0, 0) };
        _captureTextCheck = new CheckBox { Content = "读取上屏文字（关闭则仅统计物理按键）", IsChecked = _config.CaptureText, Margin = new Thickness(0, 10, 0, 0) };
        _exclusionInput = new TextBox { Width = 220 };
        _customExclusionsPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _settingsFeedbackText = new TextBlock { Text = "", FontSize = 12, Foreground = DarkTheme.GreenBrush, Margin = new Thickness(0, 10, 0, 0) };
        _tabRoots[2] = BuildSettingsTab();

        Grid tabHost = new();
        for (int i = 0; i < _tabRoots.Length; i++)
        {
            _tabRoots[i].Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            tabHost.Children.Add(_tabRoots[i]);
        }

        ScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 14, 20, 18),
            Content = tabHost,
        };
        Grid.SetRow(scroll, 1);
        mainArea.Children.Add(scroll);

        Grid.SetColumn(mainArea, 1);
        shell.Children.Add(mainArea);
        Content = shell;

        WireEvents();
        SetPaused(isPausedInitial);
        SetMiniHudChecked(_config.ShowMiniHud);
        SelectTab(0);

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += (_, _) => OnLiveTimerTick();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _statsTimer.Tick += async (_, _) => await ReloadStatsAsync();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _liveTimer.Start();
                _statsTimer.Start();
                RefreshLiveTick();
                _ = ReloadStatsAsync();
            }
            else
            {
                _liveTimer.Stop();
                _statsTimer.Stop();
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
    public event Action<bool>? MiniHudToggled;
    public event Action<UserConfig>? ConfigChanged;
    public event Action<string>? OpenWebRequested;
    public event Action? DataDirectoryRequested;

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

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    public void SelectTab(int index)
    {
        if (index < 0 || index >= _tabRoots.Length)
        {
            return;
        }

        string[] titles = ["实时监控", "弱键与分布", "设置"];
        _pageTitleText.Text = titles[index];
        _rangeToolbarPanel.Visibility = index == 2 ? Visibility.Collapsed : Visibility.Visible;

        for (int i = 0; i < _tabRoots.Length; i++)
        {
            bool active = i == index;
            _tabRoots[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            _navAccents[i].Background = active ? DarkTheme.BlueBrush : Brushes.Transparent;
            _navButtons[i].Background = active ? DarkTheme.ElevatedBrush : Brushes.Transparent;
            _navButtons[i].Foreground = active ? DarkTheme.TextBrush : DarkTheme.DimBrush;
            _navButtons[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_isViewerMode)
        {
            _sideStatusDot.Fill = DarkTheme.BlueBrush;
            _sideStatusText.Text = "只读视图 · 同步中";
            _sideStatusText.Foreground = DarkTheme.BlueBrush;
            _primaryBottomBtn.Content = "升级后台托盘服务";
        }
        else
        {
            _sideStatusDot.Fill = paused ? DarkTheme.FaintBrush : DarkTheme.GreenBrush;
            _sideStatusText.Text = paused ? "采集已暂停" : "录制中";
            _sideStatusText.Foreground = paused ? DarkTheme.DimBrush : DarkTheme.GreenBrush;
            _primaryBottomBtn.Content = paused ? "继续录制" : "暂停录制";
        }
    }

    public void SetMiniHudChecked(bool enabled)
    {
        _suppressSettingEvents = true;
        _config.ShowMiniHud = enabled;
        _miniHudCheck.IsChecked = enabled;
        _suppressSettingEvents = false;
    }

    public async Task ReloadStatsAsync()
    {
        if (_isLoadingStats)
        {
            return;
        }

        _isLoadingStats = true;
        try
        {
            DesktopSnapshot snap = await DesktopStatsService.LoadAsync(_eventsDir, _range, _config.ImeOverride);
            _telemetry.SeedFromTodayCount(snap.TodayKeystrokesFromDisk, snap.MaxTodayEventTs);
            SeedTelemetryFeedFromSnapshot(snap);
            ApplySnapshot(snap);
        }
        catch (Exception)
        {
            // 后台读取异常时不破坏现有展示
        }
        finally
        {
            _isLoadingStats = false;
        }
    }

    public void ApplySnapshot(DesktopSnapshot snap)
    {
        _snapshot = snap;
        RefreshLiveMetricsDisplay(snap, _telemetry.Snapshot());

        if (snap.AccuracyReport.Triggered)
        {
            string topNames = string.Join("、", snap.AccuracyReport.TopErrorKeys.Take(3).Select(k => k.Name));
            _guardText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"准确率提醒：当前 {snap.AccuracyReport.AccuracyPercent:0.0}%（低于 {snap.AccuracyReport.ThresholdPercent:0.#}%）。主要误按：{topNames}，建议适当放慢节奏保准。");
            _guardBanner.Visibility = Visibility.Visible;
        }
        else
        {
            _guardBanner.Visibility = Visibility.Collapsed;
        }

        RebuildTrendChart(snap);
        _heatmap.UpdateData(snap.KeysByVk);
        RebuildWeakList(snap);
        RebuildAppsList(snap);
    }

    public void RefreshLiveTick()
    {
        LiveTelemetrySnapshot live = _telemetry.Snapshot();
        if (_snapshot is not null)
        {
            RefreshLiveMetricsDisplay(_snapshot, live);
        }

        RebuildLiveFeed(live);
    }

    private void OnLiveTimerTick()
    {
        if (_isViewerMode)
        {
            _telemetry.PollTodayFile(_eventsDir);
        }

        RefreshLiveTick();
    }

    private void RefreshLiveMetricsDisplay(DesktopSnapshot snap, LiveTelemetrySnapshot live)
    {
        if (_range == StatsRange.Today)
        {
            long displayKeys = Math.Max(snap.TodayKeystrokesFromDisk, live.TodayKeystrokes);
            _kpiKeysValue.Text = displayKeys.ToString("N0", CultureInfo.InvariantCulture);
            _kpiKeysSub.Text = !string.IsNullOrEmpty(live.CurrentExe)
                ? string.Create(CultureInfo.InvariantCulture, $"{live.CurrentExe} · 上屏 {snap.Commits:N0}")
                : string.Create(CultureInfo.InvariantCulture, $"已统计 {snap.Commits:N0} 条上屏 · {snap.Chars:N0} 字");
        }
        else
        {
            _kpiKeysValue.Text = snap.Keystrokes.ToString("N0", CultureInfo.InvariantCulture);
            _kpiKeysSub.Text = string.Create(CultureInfo.InvariantCulture, $"今日实时：{live.TodayKeystrokes:N0} 键 · {snap.DaysCount} 天");
        }

        _kpiWpmValue.Text = snap.Wpm > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{snap.Wpm:0.0} wpm")
            : "0 wpm";
        string liveRate = _paused
            ? "已暂停"
            : live.LiveWpm > 0 ? $"{live.LiveWpm} wpm" : "待机";
        double mins = (snap.TextActiveMs > 0 ? snap.TextActiveMs : snap.ActiveMs) / 60000.0;
        _kpiWpmSub.Text = string.Create(CultureInfo.InvariantCulture, $"实时 {liveRate} · 活跃 {mins:0.0} 分");

        if (!string.IsNullOrEmpty(live.LastCorrectionText))
        {
            _kpiRightLabel.Text = "刚刚发生纠错";
            _kpiRightValue.Text = live.LastCorrectionText;
            _kpiRightValue.Foreground = DarkTheme.AmberBrush;
            _kpiRightSub.Text = string.Create(CultureInfo.InvariantCulture, $"准确率 {snap.AccuracyPct:0.0}% · {(string.IsNullOrEmpty(live.CurrentExe) ? "8 秒内捕获" : live.CurrentExe)}");
        }
        else
        {
            _kpiRightLabel.Text = "准确率";
            _kpiRightValue.Text = string.Create(CultureInfo.InvariantCulture, $"{snap.AccuracyPct:0.0}%");
            _kpiRightValue.Foreground = DarkTheme.TextBrush;
            _kpiRightSub.Text = string.Create(CultureInfo.InvariantCulture, $"确认纠错 {snap.Typos:N0} 次");
        }
    }

    private Border BuildSidebar()
    {
        DockPanel layout = new() { LastChildFill = true };

        StackPanel top = new() { Margin = new Thickness(16, 18, 14, 14) };
        top.Children.Add(new TextBlock
        {
            Text = "KeyTrace",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
        });

        StackPanel statusRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        statusRow.Children.Add(_sideStatusDot);
        statusRow.Children.Add(_sideStatusText);
        top.Children.Add(statusRow);

        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        StackPanel bottom = new() { Margin = new Thickness(12, 12, 12, 16) };
        bottom.Children.Add(_primaryBottomBtn);

        Button webBtn = DarkTheme.CreateButton("打开网页练习", compact: true);
        webBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        webBtn.Click += (_, _) => OpenWebRequested?.Invoke(_config.WebPracticeUrl);
        bottom.Children.Add(webBtn);

        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);

        StackPanel navList = new() { Margin = new Thickness(8, 4, 8, 8) };
        string[] labels = ["实时监控", "弱键与分布", "设置"];
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            Border accent = new()
            {
                Width = 3,
                Height = 17,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            };
            TextBlock label = new()
            {
                Text = labels[i],
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
            };

            StackPanel btnContent = new() { Orientation = Orientation.Horizontal };
            btnContent.Children.Add(accent);
            btnContent.Children.Add(label);

            Button navBtn = new()
            {
                Content = btnContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 10, 10, 10),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand,
                Foreground = DarkTheme.DimBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Style = CreateNavButtonStyle(),
            };
            navBtn.Click += (_, _) => SelectTab(idx);

            _navAccents[i] = accent;
            _navButtons[i] = navBtn;
            navList.Children.Add(navBtn);
        }

        layout.Children.Add(navList);

        return new Border
        {
            Background = DarkTheme.SidebarBrush,
            BorderBrush = DarkTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = layout,
        };
    }

    private Border BuildTopBar()
    {
        DockPanel bar = new() { LastChildFill = false };
        DockPanel.SetDock(_pageTitleText, Dock.Left);
        bar.Children.Add(_pageTitleText);

        _todayRangeBtn.Margin = new Thickness(0, 0, 6, 0);
        _days7RangeBtn.Margin = new Thickness(0, 0, 8, 0);

        Button refreshBtn = DarkTheme.CreateButton("刷新", compact: true);
        refreshBtn.Click += async (_, _) => await ReloadStatsAsync();

        _rangeToolbarPanel.Children.Add(_todayRangeBtn);
        _rangeToolbarPanel.Children.Add(_days7RangeBtn);
        _rangeToolbarPanel.Children.Add(refreshBtn);
        DockPanel.SetDock(_rangeToolbarPanel, Dock.Right);
        bar.Children.Add(_rangeToolbarPanel);

        return new Border
        {
            Background = DarkTheme.BgBrush,
            BorderBrush = DarkTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 12, 22, 12),
            Child = bar,
        };
    }

    private UIElement BuildMonitorTab()
    {
        StackPanel root = new();

        UniformGrid kpis = new() { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
        kpis.Children.Add(BuildKpiCard("击键", _kpiKeysValue, _kpiKeysSub, new Thickness(0, 0, 12, 0)));
        kpis.Children.Add(BuildKpiCard("速度", _kpiWpmValue, _kpiWpmSub, new Thickness(0, 0, 12, 0)));
        kpis.Children.Add(BuildKpiCard(_kpiRightLabel, _kpiRightValue, _kpiRightSub, new Thickness(0)));
        root.Children.Add(kpis);

        root.Children.Add(_guardBanner);

        StackPanel chartStack = new();
        DockPanel chartHead = new() { LastChildFill = false };
        DockPanel.SetDock(_chartTitleText, Dock.Left);
        DockPanel.SetDock(_chartMetaText, Dock.Right);
        chartHead.Children.Add(_chartTitleText);
        chartHead.Children.Add(_chartMetaText);
        chartStack.Children.Add(chartHead);
        chartStack.Children.Add(_chartGrid);

        Border chartCard = DarkTheme.CreateCard(chartStack, new Thickness(14));
        chartCard.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(chartCard);

        StackPanel feedBody = new();
        DockPanel feedHead = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        TextBlock feedTitle = new()
        {
            Text = "实时记录流",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(feedTitle, Dock.Left);
        DockPanel.SetDock(_hideLiveCheckOverview, Dock.Right);
        feedHead.Children.Add(feedTitle);
        feedHead.Children.Add(_hideLiveCheckOverview);
        feedBody.Children.Add(feedHead);
        feedBody.Children.Add(_liveFeedPanel);

        root.Children.Add(DarkTheme.CreateCard(feedBody, new Thickness(14)));
        return root;
    }

    private UIElement BuildWeakAndAppsTab()
    {
        StackPanel root = new();

        DockPanel heatBar = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        StackPanel leftControls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftControls.Children.Add(new TextBlock
        {
            Text = "按键热力图",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        });
        leftControls.Children.Add(_showSpLabelsCheck);
        DockPanel.SetDock(leftControls, Dock.Left);
        heatBar.Children.Add(leftControls);

        StackPanel legend = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(LegendChip(DarkTheme.HeatNoneBrush, "无错"));
        legend.Children.Add(LegendChip(DarkTheme.HeatMildBrush, "轻微"));
        legend.Children.Add(LegendChip(DarkTheme.HeatWarnBrush, "偏弱"));
        legend.Children.Add(LegendChip(DarkTheme.HeatSevereBrush, "明显弱"));
        DockPanel.SetDock(legend, Dock.Right);
        heatBar.Children.Add(legend);

        root.Children.Add(heatBar);

        _heatmap.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(_heatmap);

        Grid bottom = new();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel weakStack = new();
        weakStack.Children.Add(DarkTheme.SectionHeader("最弱的 6 个键"));
        weakStack.Children.Add(_weakListPanel);
        Border weakCard = DarkTheme.CreateCard(weakStack, new Thickness(14, 12, 14, 10));
        Grid.SetColumn(weakCard, 0);

        StackPanel appsStack = new();
        appsStack.Children.Add(DarkTheme.SectionHeader("常用软件 Top 6"));
        appsStack.Children.Add(_appsListPanel);
        Border appsCard = DarkTheme.CreateCard(appsStack, new Thickness(14, 12, 14, 10));
        Grid.SetColumn(appsCard, 2);

        bottom.Children.Add(weakCard);
        bottom.Children.Add(appsCard);
        root.Children.Add(bottom);

        return root;
    }

    private UIElement BuildSettingsTab()
    {
        StackPanel root = new() { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

        StackPanel recStack = new();
        recStack.Children.Add(DarkTheme.SectionHeader("录制与显示"));
        recStack.Children.Add(_miniHudCheck);
        recStack.Children.Add(_hideLiveCheckSettings);
        recStack.Children.Add(_captureTextCheck);
        Border recCard = DarkTheme.CreateCard(recStack);
        recCard.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(recCard);

        StackPanel excStack = new();
        excStack.Children.Add(DarkTheme.SectionHeader("隐私排除与维护"));
        excStack.Children.Add(new TextBlock
        {
            Text = "已内置排除 1Password / KeePass / Bitwarden 等 9 款密码管理器，命中时不记录任何按键与文字。",
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        StackPanel addRow = new() { Orientation = Orientation.Horizontal };
        _exclusionInput.Margin = new Thickness(0, 0, 8, 0);
        _exclusionInput.ToolTip = "输入进程文件名，例如 bank.exe";
        Button addBtn = DarkTheme.CreateButton("添加排除进程", compact: true);
        addBtn.Click += (_, _) => AddCustomExclusion();
        addRow.Children.Add(_exclusionInput);
        addRow.Children.Add(addBtn);
        addRow.Children.Add(new TextBlock
        {
            Text = "例如 bank.exe",
            FontSize = 12,
            Foreground = DarkTheme.FaintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        });
        excStack.Children.Add(addRow);
        excStack.Children.Add(_customExclusionsPanel);
        RebuildCustomExclusionChips();

        StackPanel actionRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        Button dataBtn = DarkTheme.CreateButton("打开数据目录", compact: true);
        dataBtn.Margin = new Thickness(0, 0, 8, 0);
        dataBtn.Click += (_, _) => DataDirectoryRequested?.Invoke();

        Button taskBtn = DarkTheme.CreateButton("安装/更新后台托盘服务", compact: true);
        taskBtn.Click += (_, _) => RunInstallTaskScript();

        actionRow.Children.Add(dataBtn);
        actionRow.Children.Add(taskBtn);
        excStack.Children.Add(actionRow);
        excStack.Children.Add(_settingsFeedbackText);

        root.Children.Add(DarkTheme.CreateCard(excStack));
        return root;
    }

    private void WireEvents()
    {
        _todayRangeBtn.Click += async (_, _) => await SwitchRangeAsync(StatsRange.Today);
        _days7RangeBtn.Click += async (_, _) => await SwitchRangeAsync(StatsRange.Days7);

        _showSpLabelsCheck.Checked += (_, _) => _heatmap.ShowShuangpinLabels = true;
        _showSpLabelsCheck.Unchecked += (_, _) => _heatmap.ShowShuangpinLabels = false;

        _hideLiveCheckOverview.Checked += (_, _) => OnHideLiveToggled(true);
        _hideLiveCheckOverview.Unchecked += (_, _) => OnHideLiveToggled(false);
        _hideLiveCheckSettings.Checked += (_, _) => OnHideLiveToggled(true);
        _hideLiveCheckSettings.Unchecked += (_, _) => OnHideLiveToggled(false);

        _miniHudCheck.Checked += (_, _) =>
        {
            if (!_suppressSettingEvents)
            {
                MiniHudToggled?.Invoke(true);
            }
        };
        _miniHudCheck.Unchecked += (_, _) =>
        {
            if (!_suppressSettingEvents)
            {
                MiniHudToggled?.Invoke(false);
            }
        };

        _captureTextCheck.Checked += (_, _) => OnCaptureTextToggled(true);
        _captureTextCheck.Unchecked += (_, _) => OnCaptureTextToggled(false);
    }

    private async Task SwitchRangeAsync(StatsRange next)
    {
        if (_range == next)
        {
            return;
        }

        _range = next;
        DarkTheme.SetPillActive(_todayRangeBtn, next == StatsRange.Today);
        DarkTheme.SetPillActive(_days7RangeBtn, next == StatsRange.Days7);
        await ReloadStatsAsync();
    }

    private void OnHideLiveToggled(bool hide)
    {
        if (_suppressSettingEvents)
        {
            return;
        }

        _suppressSettingEvents = true;
        _config.HideLiveText = hide;
        _hideLiveCheckOverview.IsChecked = hide;
        _hideLiveCheckSettings.IsChecked = hide;
        _suppressSettingEvents = false;

        SaveConfigAndNotify(hide ? "已开启隐私遮罩。" : "已关闭隐私遮罩。");
        RefreshLiveTick();
    }

    private void OnCaptureTextToggled(bool capture)
    {
        if (_suppressSettingEvents)
        {
            return;
        }

        _config.CaptureText = capture;
        SaveConfigAndNotify(_isViewerMode
            ? "已保存配置（只读视图下重启后台服务后生效）。"
            : (capture ? "已开启读取上屏文字。" : "已切换为仅记录物理按键。"));
    }

    private void AddCustomExclusion()
    {
        string name = _exclusionInput.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (!_config.ExtraExclusions.Contains(name, StringComparer.OrdinalIgnoreCase)
            && !ExclusionList.BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _config.ExtraExclusions.Add(name);
            _exclusionInput.Text = "";
            RebuildCustomExclusionChips();
            SaveConfigAndNotify($"已添加排除进程：{name}");
        }
        else
        {
            _exclusionInput.Text = "";
            SetSettingsFeedback($"{name} 已在排除名单中。");
        }
    }

    private void RebuildCustomExclusionChips()
    {
        _customExclusionsPanel.Children.Clear();
        if (_config.ExtraExclusions.Count == 0)
        {
            _customExclusionsPanel.Children.Add(new TextBlock
            {
                Text = "尚未添加自定义排除进程。",
                FontSize = 12,
                Foreground = DarkTheme.FaintBrush,
                Margin = new Thickness(0, 2, 0, 0),
            });
            return;
        }

        foreach (string item in _config.ExtraExclusions)
        {
            string captured = item;
            StackPanel chip = new() { Orientation = Orientation.Horizontal };
            chip.Children.Add(new TextBlock
            {
                Text = captured,
                FontSize = 12,
                FontFamily = DarkTheme.MonoFont,
                Foreground = DarkTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Button removeBtn = new()
            {
                Content = "×",
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(3, 0, 2, 0),
            };
            removeBtn.Click += (_, _) =>
            {
                _config.ExtraExclusions.RemoveAll(s => string.Equals(s, captured, StringComparison.OrdinalIgnoreCase));
                RebuildCustomExclusionChips();
                SaveConfigAndNotify($"已移除排除进程：{captured}");
            };
            chip.Children.Add(removeBtn);

            _customExclusionsPanel.Children.Add(new Border
            {
                Background = DarkTheme.ElevatedBrush,
                BorderBrush = DarkTheme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 6),
                Child = chip,
            });
        }
    }

    private void SaveConfigAndNotify(string message)
    {
        try
        {
            _config.Save(_baseDir);
            ConfigChanged?.Invoke(_config);
            SetSettingsFeedback(message);
        }
        catch (Exception ex)
        {
            SetSettingsFeedback($"保存配置失败：{ex.Message}", isError: true);
        }
    }

    private void SetSettingsFeedback(string message, bool isError = false)
    {
        _settingsFeedbackText.Text = message;
        _settingsFeedbackText.Foreground = isError ? DarkTheme.RedBrush : DarkTheme.GreenBrush;
    }

    private void RebuildTrendChart(DesktopSnapshot snap)
    {
        _chartGrid.Children.Clear();
        _chartGrid.ColumnDefinitions.Clear();

        if (_range == StatsRange.Today)
        {
            _chartTitleText.Text = "今日分时段击键 (24 小时)";
            IReadOnlyList<HourlyPoint> hours = snap.HourlySeries;
            int maxKeys = Math.Max(1, hours.Max(h => h.Keystrokes));
            int peakHour = hours.OrderByDescending(h => h.Keystrokes).FirstOrDefault()?.Hour ?? 0;
            _chartMetaText.Text = snap.TodayKeystrokesFromDisk > 0
                ? string.Create(CultureInfo.InvariantCulture, $"峰值 {peakHour:00}:00（{maxKeys:N0} 键）")
                : "今日暂无击键";

            for (int i = 0; i < hours.Count; i++)
            {
                _chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                HourlyPoint pt = hours[i];
                string xLabel = pt.Hour % 3 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{pt.Hour:00}") : "";
                string tip = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pt.Hour:00}:00 – {pt.Hour:00}:59\n击键：{pt.Keystrokes:N0}\n确认纠错：{pt.Typos}");
                FrameworkElement bar = BuildBarColumn(pt.Keystrokes, maxKeys, pt.Typos > 0, xLabel, tip);
                Grid.SetColumn(bar, i);
                _chartGrid.Children.Add(bar);
            }

            return;
        }

        _chartTitleText.Text = "近 7 天每日击键";
        IReadOnlyList<DailyPoint> days = snap.DailySeries;
        if (days.Count == 0)
        {
            _chartMetaText.Text = "暂无每日统计";
            _chartGrid.Children.Add(EmptyNotice("当前范围没有每日日志"));
            return;
        }

        int maxDayKeys = Math.Max(1, days.Max(d => d.Keystrokes));
        _chartMetaText.Text = string.Create(CultureInfo.InvariantCulture, $"{days.Count} 天 · 最高 {maxDayKeys:N0} 键/日");

        for (int i = 0; i < days.Count; i++)
        {
            _chartGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DailyPoint d = days[i];
            string xLabel = d.Day.ToString("MM-dd", CultureInfo.InvariantCulture);
            string tip = string.Create(
                CultureInfo.InvariantCulture,
                $"{d.Day:yyyy-MM-dd}\n击键：{d.Keystrokes:N0}\n确认纠错：{d.Typos:N0}\n准确率：{d.AccuracyPct:0.0}%");
            FrameworkElement bar = BuildBarColumn(d.Keystrokes, maxDayKeys, d.Typos > 0, xLabel, tip);
            Grid.SetColumn(bar, i);
            _chartGrid.Children.Add(bar);
        }
    }

    private static FrameworkElement BuildBarColumn(int value, int max, bool hasTypos, string xLabel, string tooltip)
    {
        Grid col = new() { Margin = new Thickness(2, 0, 2, 0), ToolTip = tooltip, Background = Brushes.Transparent };
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        const double maxBarPx = 98.0;
        double h = value <= 0 ? 3.0 : Math.Max(6.0, maxBarPx * value / max);
        Border bar = new()
        {
            Height = h,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            Background = value <= 0
                ? DarkTheme.ElevatedBrush
                : (hasTypos ? DarkTheme.GreenBrush : DarkTheme.BlueBrush),
            MaxWidth = 32,
        };
        Grid.SetRow(bar, 0);
        col.Children.Add(bar);

        TextBlock label = new()
        {
            Text = xLabel,
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        };
        Grid.SetRow(label, 1);
        col.Children.Add(label);

        return col;
    }

    private void SeedTelemetryFeedFromSnapshot(DesktopSnapshot snap)
    {
        List<LiveFeedItem> seeded = [];
        foreach (DesktopCommitItem c in snap.RecentCommits.Take(10))
        {
            seeded.Add(new LiveFeedItem(c.Ts, c.Exe, "commit", c.Text, c.Keys, null, null));
        }

        foreach (TypoRecord t in snap.RecentTypos.Take(8))
        {
            string wrong = t.Deleted is char d ? d.ToString().ToUpperInvariant() : "?";
            string right = t.Replacement is char r ? r.ToString().ToUpperInvariant() : "?";
            seeded.Add(new LiveFeedItem(t.Ts, t.Exe, "correction", null, 1, wrong, right));
        }

        seeded.Sort((a, b) => b.Ts.CompareTo(a.Ts));
        _telemetry.SeedRecentFeed(seeded);
    }

    private void RebuildLiveFeed(LiveTelemetrySnapshot live)
    {
        _liveFeedPanel.Children.Clear();
        if (live.Feed.Count == 0)
        {
            _liveFeedPanel.Children.Add(EmptyNotice("等待输入... 在任意窗口打字，这里会实时滚出上屏与纠错。"));
            return;
        }

        foreach (LiveFeedItem item in live.Feed.Take(15))
        {
            bool isCorrection = item.Kind == "correction";
            string timeText = DateTimeOffset.FromUnixTimeMilliseconds(item.Ts).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            Grid rowGrid = new();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock timeEl = new()
            {
                Text = timeText,
                FontFamily = DarkTheme.MonoFont,
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(timeEl, 0);

            TextBlock exeEl = new()
            {
                Text = item.Exe,
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(exeEl, 1);

            Border badge = new()
            {
                Background = isCorrection ? DarkTheme.RedBgBrush : DarkTheme.BlueBgBrush,
                BorderBrush = isCorrection ? DarkTheme.RedBrush : DarkTheme.BlueBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = isCorrection
                        ? "实时纠错"
                        : string.Create(CultureInfo.InvariantCulture, $"上屏 {item.Keys}键"),
                    FontSize = 12,
                    Foreground = isCorrection ? DarkTheme.RedBrush : DarkTheme.BlueBrush,
                },
            };
            Grid.SetColumn(badge, 2);

            string bodyText;
            if (isCorrection)
            {
                bodyText = $"误按 {item.WrongKey} → 退格改按 {item.RightKey}";
            }
            else if (_config.HideLiveText)
            {
                int len = item.Text?.Length ?? 0;
                bodyText = string.Create(CultureInfo.InvariantCulture, $"[已隐藏明文 · {len} 字]");
            }
            else
            {
                bodyText = FormatSingleLinePreview(item.Text ?? "");
            }

            TextBlock contentEl = new()
            {
                Text = bodyText,
                FontSize = 12.5,
                Foreground = _config.HideLiveText && !isCorrection ? DarkTheme.FaintBrush : DarkTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(contentEl, 3);

            rowGrid.Children.Add(timeEl);
            rowGrid.Children.Add(exeEl);
            rowGrid.Children.Add(badge);
            rowGrid.Children.Add(contentEl);

            _liveFeedPanel.Children.Add(new Border
            {
                BorderBrush = DarkTheme.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 6, 2, 6),
                Child = rowGrid,
            });
        }
    }

    private void RebuildWeakList(DesktopSnapshot snap)
    {
        _weakListPanel.Children.Clear();
        if (snap.WeakLetters.Count == 0)
        {
            _weakListPanel.Children.Add(EmptyNotice("当前范围还没有足够的错按样本"));
            return;
        }

        foreach (KeyCellMetric key in snap.WeakLetters.Take(6))
        {
            Grid row = new();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border tile = new()
            {
                Width = 24,
                Height = 24,
                Background = DarkTheme.ElevatedBrush,
                BorderBrush = DarkTheme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Child = new TextBlock
                {
                    Text = key.Name,
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = DarkTheme.TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(tile, 0);

            TextBlock errInfo = new()
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"错率 {key.TypoRate * 100:0.0}%  ({key.Typos}/{key.Presses})"),
                FontSize = 12.5,
                Foreground = DarkTheme.RedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
            };
            Grid.SetColumn(errInfo, 1);

            TextBlock speedInfo = new()
            {
                Text = key.MedianIkiMs > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"中位 {key.MedianIkiMs:0} ms")
                    : "中位 -- ms",
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(speedInfo, 2);

            row.Children.Add(tile);
            row.Children.Add(errInfo);
            row.Children.Add(speedInfo);

            _weakListPanel.Children.Add(new Border
            {
                BorderBrush = DarkTheme.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 5, 2, 5),
                ToolTip = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{key.Name} · 击键 {key.Presses:N0} · 纠错 {key.Typos:N0} ({key.TypoRate * 100:0.0}%)\n等效速度 {(key.Wpm > 0 ? $"{key.Wpm:0.0} wpm" : "--")}"),
                Child = row,
            });
        }
    }

    private void RebuildAppsList(DesktopSnapshot snap)
    {
        _appsListPanel.Children.Clear();
        if (snap.Apps.Count == 0)
        {
            _appsListPanel.Children.Add(EmptyNotice("当前范围暂无应用记录"));
            return;
        }

        _appsListPanel.Children.Add(BuildAppRow("应用", "总击键", "准确率", isHeader: true, tooltip: null));
        foreach (DesktopAppMetric app in snap.Apps.Take(6))
        {
            string tip = string.Create(
                CultureInfo.InvariantCulture,
                $"{app.Exe}\n上屏 {app.Commits:N0} 条 · 确认纠错 {app.Typos:N0} 次\n文本读取：{(app.TextAvailable ? "可读取" : "盲区(仅计键)")}");
            _appsListPanel.Children.Add(BuildAppRow(
                app.Exe,
                app.Keystrokes.ToString("N0", CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{app.AccuracyPct:0.0}%"),
                isHeader: false,
                tooltip: tip));
        }
    }

    private static FrameworkElement BuildAppRow(string name, string keys, string acc, bool isHeader, string? tooltip)
    {
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });

        Brush fg = isHeader ? DarkTheme.DimBrush : DarkTheme.TextBrush;
        FontWeight fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;

        TextBlock c1 = new()
        {
            Text = name,
            FontSize = 12.5,
            FontWeight = fw,
            Foreground = fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(c1, 0);

        TextBlock c2 = new()
        {
            Text = keys,
            FontSize = 12.5,
            FontWeight = fw,
            Foreground = fg,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(c2, 1);

        TextBlock c3 = new()
        {
            Text = acc,
            FontSize = 12.5,
            FontWeight = fw,
            Foreground = fg,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(c3, 2);

        row.Children.Add(c1);
        row.Children.Add(c2);
        row.Children.Add(c3);

        Border border = new()
        {
            BorderBrush = DarkTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, isHeader ? 4 : 7, 2, isHeader ? 6 : 7),
            Background = Brushes.Transparent,
            Child = row,
        };
        if (tooltip is not null)
        {
            border.ToolTip = tooltip;
        }

        return border;
    }

    private void RunInstallTaskScript()
    {
        string[] candidates =
        [
            System.IO.Path.Combine(AppContext.BaseDirectory, "scripts", "install-task.bat"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "keytrace", "scripts", "install-task.bat"),
        ];

        string? script = candidates.FirstOrDefault(File.Exists);
        if (script is null)
        {
            SetSettingsFeedback("找不到 scripts\\install-task.bat。", isError: true);
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
            SetSettingsFeedback("已调起安装/更新脚本，确认 UAC 后将升级后台托盘服务。");
        }
        catch (Exception ex)
        {
            SetSettingsFeedback($"启动脚本失败：{ex.Message}", isError: true);
        }
    }

    private static Border BuildKpiCard(object labelOrText, TextBlock valueBlock, TextBlock subBlock, Thickness margin)
    {
        StackPanel stack = new();
        UIElement header = labelOrText is UIElement el
            ? el
            : new TextBlock { Text = labelOrText.ToString() ?? "", FontSize = 12, Foreground = DarkTheme.DimBrush };
        stack.Children.Add(header);
        stack.Children.Add(valueBlock);
        stack.Children.Add(subBlock);

        Border card = DarkTheme.CreateCard(stack, new Thickness(14, 12, 14, 12));
        card.Margin = margin;
        return card;
    }

    private static TextBlock KpiValueText(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Foreground = DarkTheme.TextBrush,
        Margin = new Thickness(0, 3, 0, 2),
    };

    private static TextBlock KpiSubText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = DarkTheme.DimBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static StackPanel LegendChip(Brush swatch, string label)
    {
        StackPanel sp = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        sp.Children.Add(new Border
        {
            Width = 11,
            Height = 9,
            Background = swatch,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }

    private static TextBlock EmptyNotice(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = DarkTheme.FaintBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 6, 2, 6),
    };

    private static string FormatSingleLinePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return string.Join(" · ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static Style CreateNavButtonStyle()
    {
        Style style = new(typeof(Button));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(WpfControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));

        FrameworkElementFactory bd = new(typeof(Border));
        bd.Name = "nbd";
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfControl.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(WpfControl.PaddingProperty));

        FrameworkElementFactory cp = new(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        ControlTemplate tpl = new(typeof(Button)) { VisualTree = bd };
        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, DarkTheme.HoverBrush, "nbd"));
        tpl.Triggers.Add(hover);

        style.Setters.Add(new Setter(WpfControl.TemplateProperty, tpl));
        return style;
    }
}
