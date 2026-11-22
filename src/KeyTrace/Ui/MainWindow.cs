using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KeyTrace.Analysis;
using KeyTrace.Collect;
using WpfControl = System.Windows.Controls.Control;

namespace KeyTrace.Ui;

/// <summary>
/// KeyTrace 独立原生暗色桌面主窗口：
/// 脱离浏览器也可独立查看今日概览、实时心跳、键盘热力图、应用分布与隐私配置。
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly record struct ImeOption(string Value, string Label);

    private static readonly ImeOption[] ImeOptions =
    [
        new("auto", "自动检测 (读取系统注册表)"),
        new("shuangpin/xiaohe", "小鹤双拼"),
        new("shuangpin/ziranma", "自然码双拼"),
        new("shuangpin/microsoft", "微软双拼"),
        new("shuangpin/sogou", "搜狗双拼"),
        new("full-pinyin", "全拼"),
    ];

    private readonly string _eventsDir;
    private readonly string _baseDir;
    private readonly UserConfig _config;
    private readonly LiveTelemetry _telemetry;
    private readonly bool _isViewerMode;

    private readonly Button[] _navButtons = new Button[4];
    private readonly Border[] _navAccents = new Border[4];
    private readonly UIElement[] _tabRoots = new UIElement[4];

    private readonly Button _todayRangeBtn;
    private readonly Button _days7RangeBtn;
    private readonly Button _allRangeBtn;
    private readonly TextBlock _pageTitleText;
    private readonly TextBlock _imeBadgeText;
    private readonly TextBlock _updatedText;

    private readonly Ellipse _sideStatusDot;
    private readonly TextBlock _sideStatusText;
    private readonly Button _hudToggleBtn;
    private readonly Button _pauseToggleBtn;

    // Tab 0: Overview
    private readonly TextBlock _kpiKeysValue;
    private readonly TextBlock _kpiKeysSub;
    private readonly TextBlock _kpiWpmValue;
    private readonly TextBlock _kpiWpmSub;
    private readonly TextBlock _kpiAccValue;
    private readonly TextBlock _kpiAccSub;
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

    // Tab 1: Heatmap
    private readonly Button _modeSpBtn;
    private readonly Button _modeEnBtn;
    private readonly Button _modeSymBtn;
    private readonly KeyboardHeatmapControl _heatmap;
    private readonly TextBlock _selectedKeyInfoText;
    private readonly Button _selectedKeyOfflineBtn;
    private readonly Button _selectedKeyWebBtn;
    private readonly TextBlock _weakSectionTitle;
    private readonly UniformGrid _weakCardsGrid;

    // Tab 2: Details
    private readonly StackPanel _appsTablePanel;
    private readonly StackPanel _typosFeedPanel;
    private readonly StackPanel _commitsFeedPanel;

    // Tab 3: Settings
    private readonly CheckBox _miniHudCheck;
    private readonly CheckBox _hideLiveCheckSettings;
    private readonly CheckBox _captureTextCheck;
    private readonly ComboBox _imeCombo;
    private readonly TextBox _webUrlInput;
    private readonly TextBox _exclusionInput;
    private readonly WrapPanel _customExclusionsPanel;
    private readonly TextBlock _taskStatusText;
    private readonly TextBlock _settingsFeedbackText;

    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _statsTimer;

    private StatsRange _range = StatsRange.Today;
    private HeatmapDisplayMode _heatmapMode = HeatmapDisplayMode.Shuangpin;
    private int _activeTab;
    private bool _paused;
    private bool _suppressSettingEvents;
    private bool _isLoadingStats;
    private char? _selectedWeakChar;
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
        Width = 1080;
        Height = 720;
        MinWidth = 880;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        DarkTheme.ApplyDarkTitleBar(this);
        DarkTheme.ApplyWindowResources(this);

        // 主框架：左侧导航 + 右侧（顶部工具栏 + 内容滚动区）
        Grid shell = new();
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(204) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---------- 左侧导航栏 ----------
        _sideStatusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        _sideStatusText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
        _hudToggleBtn = DarkTheme.CreateButton("悬浮条：关闭", compact: true);
        _hudToggleBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _hudToggleBtn.Click += (_, _) => MiniHudToggled?.Invoke(!_config.ShowMiniHud);

        _pauseToggleBtn = DarkTheme.CreateButton("暂停录制", compact: true);
        _pauseToggleBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pauseToggleBtn.Click += (_, _) => TogglePauseRequested?.Invoke();

        Border sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        shell.Children.Add(sidebar);

        // ---------- 右侧主区域 ----------
        Grid mainArea = new();
        mainArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _pageTitleText = new TextBlock
        {
            Text = "今日概览",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _todayRangeBtn = DarkTheme.CreatePillButton("今日", true);
        _days7RangeBtn = DarkTheme.CreatePillButton("近 7 天", false);
        _allRangeBtn = DarkTheme.CreatePillButton("全部", false);
        _imeBadgeText = new TextBlock
        {
            Text = "小鹤双拼",
            FontSize = 12,
            Foreground = DarkTheme.BlueBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _updatedText = new TextBlock
        {
            Text = "尚未加载",
            FontSize = 12,
            Foreground = DarkTheme.FaintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };

        Border topBar = BuildTopBar();
        Grid.SetRow(topBar, 0);
        mainArea.Children.Add(topBar);

        // 初始化 4 个标签页视图
        _kpiKeysValue = KpiValueText("0");
        _kpiKeysSub = KpiSubText("正在读取...");
        _kpiWpmValue = KpiValueText("0 wpm");
        _kpiWpmSub = KpiSubText("近 30 秒实时：待机");
        _kpiAccValue = KpiValueText("100.0%");
        _kpiAccSub = KpiSubText("确认错击 0");
        _kpiRightLabel = new TextBlock { Text = "退格纠错", FontSize = 12, Foreground = DarkTheme.DimBrush };
        _kpiRightValue = KpiValueText("0");
        _kpiRightSub = KpiSubText("今日日志 0 KB");
        _guardText = new TextBlock { FontSize = 12.5, Foreground = DarkTheme.AmberBrush, TextWrapping = TextWrapping.Wrap };
        _guardBanner = new Border
        {
            Background = DarkTheme.AmberBgBrush,
            BorderBrush = DarkTheme.AmberBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 16),
            Visibility = Visibility.Collapsed,
            Child = _guardText,
        };
        _chartTitleText = new TextBlock { Text = "今日每小时击键分布", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = DarkTheme.TextBrush };
        _chartMetaText = new TextBlock { Text = "", FontSize = 12, Foreground = DarkTheme.DimBrush, VerticalAlignment = VerticalAlignment.Center };
        _chartGrid = new Grid { Height = 152, Margin = new Thickness(0, 12, 0, 0) };
        _hideLiveCheckOverview = new CheckBox { Content = "隐私：隐藏明文", IsChecked = _config.HideLiveText };
        _liveFeedPanel = new StackPanel();
        _tabRoots[0] = BuildOverviewTab();

        _modeSpBtn = DarkTheme.CreatePillButton("双拼键帽", true);
        _modeEnBtn = DarkTheme.CreatePillButton("纯英文", false);
        _modeSymBtn = DarkTheme.CreatePillButton("编程符号", false);
        _heatmap = new KeyboardHeatmapControl { Margin = new Thickness(0, 0, 0, 14) };
        _selectedKeyInfoText = new TextBlock
        {
            Text = "提示：点击键盘上任意字母键查看详情或一键生成专项练习",
            FontSize = 12.5,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _selectedKeyOfflineBtn = DarkTheme.CreateButton("离线单字练习", primary: true, compact: true);
        _selectedKeyOfflineBtn.Visibility = Visibility.Collapsed;
        _selectedKeyWebBtn = DarkTheme.CreateButton("去网页练", compact: true);
        _selectedKeyWebBtn.Visibility = Visibility.Collapsed;
        _weakSectionTitle = DarkTheme.SectionHeader("日常最容易错 / 最慢的键 (Top 12)");
        _weakCardsGrid = new UniformGrid { Columns = 3 };
        _tabRoots[1] = BuildHeatmapTab();

        _appsTablePanel = new StackPanel();
        _typosFeedPanel = new StackPanel();
        _commitsFeedPanel = new StackPanel();
        _tabRoots[2] = BuildDetailsTab();

        _miniHudCheck = new CheckBox { Content = "开启桌面悬浮状态条 (Mini HUD，可拖动、不抢输入焦点)", IsChecked = _config.ShowMiniHud };
        _hideLiveCheckSettings = new CheckBox { Content = "隐私模式：隐藏实时心跳与明细列表里的上屏明文与上下文词", IsChecked = _config.HideLiveText, Margin = new Thickness(0, 10, 0, 0) };
        _captureTextCheck = new CheckBox { Content = "启用 UIA 上屏文本与中文纠错归因（取消勾选则仅记录物理按键，不读任何文字）", IsChecked = _config.CaptureText };
        _imeCombo = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        _webUrlInput = new TextBox { Width = 320, Text = _config.WebPracticeUrl };
        _exclusionInput = new TextBox { Width = 240 };
        _customExclusionsPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _taskStatusText = new TextBlock { Text = "正在检测开机高权限计划任务状态...", FontSize = 12.5, Foreground = DarkTheme.DimBrush };
        _settingsFeedbackText = new TextBlock { Text = "", FontSize = 12.5, Foreground = DarkTheme.GreenBrush, Margin = new Thickness(0, 10, 0, 0) };
        _tabRoots[3] = BuildSettingsTab();

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
            Padding = new Thickness(24, 20, 24, 24),
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
                _ = RefreshScheduledTaskStatusAsync();
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
    }

    public void SelectTab(int index)
    {
        if (index < 0 || index >= _tabRoots.Length)
        {
            return;
        }

        _activeTab = index;
        string[] titles = ["今日概览", "键盘热力图", "应用与纠错", "设置与隐私"];
        _pageTitleText.Text = titles[index];

        for (int i = 0; i < _tabRoots.Length; i++)
        {
            bool active = i == index;
            _tabRoots[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            _navAccents[i].Background = active ? DarkTheme.BlueBrush : Brushes.Transparent;
            _navButtons[i].Background = active ? DarkTheme.ElevatedBrush : Brushes.Transparent;
            _navButtons[i].Foreground = active ? DarkTheme.TextBrush : DarkTheme.DimBrush;
            _navButtons[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (index == 3)
        {
            _ = RefreshScheduledTaskStatusAsync();
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_isViewerMode)
        {
            _sideStatusDot.Fill = DarkTheme.BlueBrush;
            _sideStatusText.Text = "只读视图 · 同步磁盘";
            _sideStatusText.Foreground = DarkTheme.BlueBrush;
            _pauseToggleBtn.IsEnabled = false;
            _pauseToggleBtn.Content = "后台进程控制录制";
        }
        else
        {
            _sideStatusDot.Fill = paused ? DarkTheme.FaintBrush : DarkTheme.GreenBrush;
            _sideStatusText.Text = paused ? "采集已暂停" : "录制活跃中";
            _sideStatusText.Foreground = paused ? DarkTheme.DimBrush : DarkTheme.GreenBrush;
            _pauseToggleBtn.IsEnabled = true;
            _pauseToggleBtn.Content = paused ? "继续录制 (Ctrl+Alt+P)" : "暂停录制 (Ctrl+Alt+P)";
        }
    }

    public void SetMiniHudChecked(bool enabled)
    {
        _suppressSettingEvents = true;
        _config.ShowMiniHud = enabled;
        _miniHudCheck.IsChecked = enabled;
        _hudToggleBtn.Content = enabled ? "悬浮条：已开启" : "悬浮条：已关闭";
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
        catch (Exception ex)
        {
            _updatedText.Text = $"加载失败：{ex.Message}";
        }
        finally
        {
            _isLoadingStats = false;
        }
    }

    public void ApplySnapshot(DesktopSnapshot snap)
    {
        _snapshot = snap;
        _imeBadgeText.Text = FormatImeLabel(snap.ImeName);
        _imeBadgeText.ToolTip = $"当前识别方案：{snap.ImeName}";
        _updatedText.Text = $"更新于 {snap.GeneratedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";

        RefreshLiveMetricsDisplay(snap, _telemetry.Snapshot());

        if (snap.AccuracyReport.Triggered)
        {
            string topNames = string.Join("、", snap.AccuracyReport.TopErrorKeys.Take(3).Select(k => k.Name));
            _guardText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"准确率护栏提醒：当前准确率 {snap.AccuracyReport.AccuracyPercent:0.0}%（低于 {snap.AccuracyReport.ThresholdPercent:0.#}% 门槛）。主要误按：{topNames}，建议适当放慢节奏保准。");
            _guardBanner.Visibility = Visibility.Visible;
        }
        else
        {
            _guardBanner.Visibility = Visibility.Collapsed;
        }

        RebuildTrendChart(snap);
        _heatmap.UpdateData(snap.KeysByVk);
        RebuildWeakCards(snap);
        RebuildAppsTable(snap);
        RebuildDetailsFeeds(snap);
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

        _kpiAccValue.Text = string.Create(CultureInfo.InvariantCulture, $"{snap.AccuracyPct:0.0}%");
        _kpiAccSub.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"确认错击 {snap.Typos:N0} · 选字错 {snap.SelectionErrors:N0}");

        if (!string.IsNullOrEmpty(live.LastCorrectionText))
        {
            _kpiRightLabel.Text = "刚刚发生纠错";
            _kpiRightValue.Text = live.LastCorrectionText;
            _kpiRightValue.Foreground = DarkTheme.AmberBrush;
            _kpiRightSub.Text = string.IsNullOrEmpty(live.CurrentExe) ? "8 秒内捕获" : $"来源：{live.CurrentExe}";
        }
        else
        {
            _kpiRightLabel.Text = "退格纠错";
            _kpiRightValue.Text = snap.Typos.ToString("N0", CultureInfo.InvariantCulture);
            _kpiRightValue.Foreground = DarkTheme.TextBrush;
            _kpiRightSub.Text = string.Create(CultureInfo.InvariantCulture, $"今日日志 {FormatBytes(snap.TodayLogBytes)}");
        }
    }

    private Border BuildSidebar()
    {
        DockPanel layout = new() { LastChildFill = true };

        StackPanel top = new() { Margin = new Thickness(16, 20, 16, 16) };
        StackPanel titleRow = new() { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "KeyTrace",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "桌面版",
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(7, 0, 0, 2),
        });
        top.Children.Add(titleRow);

        StackPanel statusRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        statusRow.Children.Add(_sideStatusDot);
        statusRow.Children.Add(_sideStatusText);
        top.Children.Add(statusRow);

        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        StackPanel bottom = new() { Margin = new Thickness(14, 12, 14, 16) };
        _hudToggleBtn.Margin = new Thickness(0, 0, 0, 8);
        bottom.Children.Add(_hudToggleBtn);
        bottom.Children.Add(_pauseToggleBtn);

        if (_isViewerMode)
        {
            bottom.Children.Add(new Border
            {
                Background = DarkTheme.AmberBgBrush,
                BorderBrush = DarkTheme.AmberBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    Text = "独立只读视图：后台已在采集，此窗口每秒增量同步磁盘日志。",
                    FontSize = 12,
                    Foreground = DarkTheme.AmberBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);

        StackPanel navList = new() { Margin = new Thickness(10, 4, 10, 8) };
        string[] labels = ["今日概览", "键盘热力图", "应用与纠错", "设置与隐私"];
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            Border accent = new()
            {
                Width = 3,
                Height = 18,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
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
            };
            navBtn.Style = CreateNavButtonStyle();
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

        StackPanel right = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(right, Dock.Right);

        _todayRangeBtn.Margin = new Thickness(0, 0, 4, 0);
        _days7RangeBtn.Margin = new Thickness(0, 0, 4, 0);
        _allRangeBtn.Margin = new Thickness(0, 0, 10, 0);

        Border imePill = new()
        {
            Background = DarkTheme.BlueBgBrush,
            BorderBrush = DarkTheme.BlueBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Child = _imeBadgeText,
        };

        Button refreshBtn = DarkTheme.CreateButton("刷新", compact: true);
        refreshBtn.Margin = new Thickness(0, 0, 8, 0);
        refreshBtn.Click += async (_, _) => await ReloadStatsAsync();

        Button webBtn = DarkTheme.CreateButton("去网页练习", primary: true, compact: true);
        webBtn.Click += (_, _) => OpenWebRequested?.Invoke(_config.WebPracticeUrl);

        right.Children.Add(_todayRangeBtn);
        right.Children.Add(_days7RangeBtn);
        right.Children.Add(_allRangeBtn);
        right.Children.Add(imePill);
        right.Children.Add(_updatedText);
        right.Children.Add(refreshBtn);
        right.Children.Add(webBtn);

        bar.Children.Add(right);

        return new Border
        {
            Background = DarkTheme.BgBrush,
            BorderBrush = DarkTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 12, 24, 12),
            Child = bar,
        };
    }

    private UIElement BuildOverviewTab()
    {
        StackPanel root = new();

        UniformGrid kpis = new() { Columns = 4, Margin = new Thickness(0, 0, 0, 16) };
        kpis.Children.Add(BuildKpiCard("有效击键", _kpiKeysValue, _kpiKeysSub, new Thickness(0, 0, 12, 0)));
        kpis.Children.Add(BuildKpiCard("平均速度", _kpiWpmValue, _kpiWpmSub, new Thickness(0, 0, 12, 0)));
        kpis.Children.Add(BuildKpiCard("准确率", _kpiAccValue, _kpiAccSub, new Thickness(0, 0, 12, 0)));
        kpis.Children.Add(BuildKpiCard(_kpiRightLabel, _kpiRightValue, _kpiRightSub, new Thickness(0)));
        root.Children.Add(kpis);

        root.Children.Add(_guardBanner);

        // 趋势图
        StackPanel chartStack = new();
        DockPanel chartHead = new() { LastChildFill = false };
        DockPanel.SetDock(_chartTitleText, Dock.Left);
        DockPanel.SetDock(_chartMetaText, Dock.Right);
        chartHead.Children.Add(_chartTitleText);
        chartHead.Children.Add(_chartMetaText);
        chartStack.Children.Add(chartHead);
        chartStack.Children.Add(_chartGrid);

        Border chartCard = DarkTheme.CreateCard(chartStack);
        chartCard.Margin = new Thickness(0, 0, 0, 16);
        root.Children.Add(chartCard);

        // 实时心跳事件流
        StackPanel feedCardBody = new();
        DockPanel feedHead = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        TextBlock feedTitle = new()
        {
            Text = "实时心跳事件流（近期上屏与纠错）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(feedTitle, Dock.Left);
        DockPanel.SetDock(_hideLiveCheckOverview, Dock.Right);
        feedHead.Children.Add(feedTitle);
        feedHead.Children.Add(_hideLiveCheckOverview);
        feedCardBody.Children.Add(feedHead);
        feedCardBody.Children.Add(_liveFeedPanel);

        root.Children.Add(DarkTheme.CreateCard(feedCardBody));
        return root;
    }

    private UIElement BuildHeatmapTab()
    {
        StackPanel root = new();

        DockPanel topControls = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        StackPanel modePills = new() { Orientation = Orientation.Horizontal };
        _modeSpBtn.Margin = new Thickness(0, 0, 6, 0);
        _modeEnBtn.Margin = new Thickness(0, 0, 6, 0);
        modePills.Children.Add(_modeSpBtn);
        modePills.Children.Add(_modeEnBtn);
        modePills.Children.Add(_modeSymBtn);
        DockPanel.SetDock(modePills, Dock.Left);
        topControls.Children.Add(modePills);

        StackPanel legend = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(LegendChip(DarkTheme.HeatNoneBrush, "无数据/零错"));
        legend.Children.Add(LegendChip(DarkTheme.HeatMildBrush, "轻微"));
        legend.Children.Add(LegendChip(DarkTheme.HeatWarnBrush, "偏弱"));
        legend.Children.Add(LegendChip(DarkTheme.HeatWeakBrush, "弱"));
        legend.Children.Add(LegendChip(DarkTheme.HeatSevereBrush, "明显弱"));
        DockPanel.SetDock(legend, Dock.Right);
        topControls.Children.Add(legend);
        root.Children.Add(topControls);

        root.Children.Add(_heatmap);

        // 选中键动作栏
        DockPanel selectedRow = new() { LastChildFill = true };
        StackPanel selectedBtns = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        _selectedKeyOfflineBtn.Margin = new Thickness(0, 0, 8, 0);
        selectedBtns.Children.Add(_selectedKeyOfflineBtn);
        selectedBtns.Children.Add(_selectedKeyWebBtn);
        DockPanel.SetDock(selectedBtns, Dock.Right);
        selectedRow.Children.Add(selectedBtns);
        selectedRow.Children.Add(_selectedKeyInfoText);

        Border selectedBar = DarkTheme.CreateCard(selectedRow, new Thickness(14, 10, 14, 10));
        selectedBar.Margin = new Thickness(0, 0, 0, 18);
        root.Children.Add(selectedBar);

        root.Children.Add(_weakSectionTitle);
        root.Children.Add(_weakCardsGrid);

        return root;
    }

    private UIElement BuildDetailsTab()
    {
        StackPanel root = new();

        StackPanel appsStack = new();
        appsStack.Children.Add(DarkTheme.SectionHeader("分应用打字分布"));
        appsStack.Children.Add(_appsTablePanel);
        Border appsCard = DarkTheme.CreateCard(appsStack);
        appsCard.Margin = new Thickness(0, 0, 0, 16);
        root.Children.Add(appsCard);

        Grid bottom = new();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel typosStack = new();
        typosStack.Children.Add(DarkTheme.SectionHeader("最近真实纠错还原"));
        typosStack.Children.Add(_typosFeedPanel);
        Border typosCard = DarkTheme.CreateCard(typosStack);
        typosCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(typosCard, 0);

        StackPanel commitsStack = new();
        commitsStack.Children.Add(DarkTheme.SectionHeader("最近上屏片段"));
        commitsStack.Children.Add(_commitsFeedPanel);
        Border commitsCard = DarkTheme.CreateCard(commitsStack);
        commitsCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(commitsCard, 2);

        bottom.Children.Add(typosCard);
        bottom.Children.Add(commitsCard);
        root.Children.Add(bottom);

        return root;
    }

    private UIElement BuildSettingsTab()
    {
        StackPanel root = new() { MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };

        // 1. 窗口与存在感
        StackPanel winStack = new();
        winStack.Children.Add(DarkTheme.SectionHeader("窗口与存在感"));
        winStack.Children.Add(_miniHudCheck);
        winStack.Children.Add(_hideLiveCheckSettings);

        StackPanel urlRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        urlRow.Children.Add(new TextBlock
        {
            Text = "网页练习地址：",
            FontSize = 12.5,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        _webUrlInput.Margin = new Thickness(0, 0, 8, 0);
        Button saveUrlBtn = DarkTheme.CreateButton("保存", compact: true);
        saveUrlBtn.Click += (_, _) =>
        {
            string val = _webUrlInput.Text.Trim();
            _config.WebPracticeUrl = string.IsNullOrEmpty(val) ? "http://localhost:3000/" : val;
            _webUrlInput.Text = _config.WebPracticeUrl;
            SaveConfigAndNotify("已更新网页练习地址。");
        };
        urlRow.Children.Add(_webUrlInput);
        urlRow.Children.Add(saveUrlBtn);
        winStack.Children.Add(urlRow);

        Border winCard = DarkTheme.CreateCard(winStack);
        winCard.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(winCard);

        // 2. 采集与输入法方案
        StackPanel collectStack = new();
        collectStack.Children.Add(DarkTheme.SectionHeader("采集与输入法方案"));
        collectStack.Children.Add(_captureTextCheck);

        foreach (ImeOption opt in ImeOptions)
        {
            _imeCombo.Items.Add(opt.Label);
        }

        string currentIme = string.IsNullOrWhiteSpace(_config.ImeOverride) ? "auto" : _config.ImeOverride;
        int selectedIdx = Array.FindIndex(ImeOptions, o => string.Equals(o.Value, currentIme, StringComparison.OrdinalIgnoreCase));
        _imeCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;

        StackPanel imeRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        imeRow.Children.Add(new TextBlock
        {
            Text = "输入法方案：",
            FontSize = 12.5,
            Foreground = DarkTheme.DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        imeRow.Children.Add(_imeCombo);
        collectStack.Children.Add(imeRow);
        collectStack.Children.Add(new TextBlock
        {
            Text = "第三方输入法（搜狗/微信/Rime 等）不在微软注册表内，建议在此手动指定对应方案，后续切换立即写入日志 meta。",
            FontSize = 12,
            Foreground = DarkTheme.FaintBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        Border collectCard = DarkTheme.CreateCard(collectStack);
        collectCard.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(collectCard);

        // 3. 进程排除名单
        StackPanel excStack = new();
        excStack.Children.Add(DarkTheme.SectionHeader("进程排除名单（密码管理器 / 敏感程序）"));
        excStack.Children.Add(new TextBlock
        {
            Text = "内置排除（始终生效）：",
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            Margin = new Thickness(0, 0, 0, 6),
        });

        WrapPanel builtInWrap = new() { Margin = new Thickness(0, 0, 0, 12) };
        foreach (string name in ExclusionList.BuiltInNames)
        {
            builtInWrap.Children.Add(new Border
            {
                Background = DarkTheme.ElevatedBrush,
                BorderBrush = DarkTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = name, FontSize = 12, FontFamily = DarkTheme.MonoFont, Foreground = DarkTheme.DimBrush },
            });
        }
        excStack.Children.Add(builtInWrap);

        StackPanel addExcRow = new() { Orientation = Orientation.Horizontal };
        _exclusionInput.Margin = new Thickness(0, 0, 8, 0);
        _exclusionInput.ToolTip = "输入可执行文件名，例如 bank.exe 或 keepass.exe";
        Button addExcBtn = DarkTheme.CreateButton("添加自定义排除", compact: true);
        addExcBtn.Click += (_, _) => AddCustomExclusion();
        addExcRow.Children.Add(_exclusionInput);
        addExcRow.Children.Add(addExcBtn);
        addExcRow.Children.Add(new TextBlock
        {
            Text = "例如 bank.exe（不含路径）",
            FontSize = 12,
            Foreground = DarkTheme.FaintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        });
        excStack.Children.Add(addExcRow);
        excStack.Children.Add(_customExclusionsPanel);
        RebuildCustomExclusionChips();

        Border excCard = DarkTheme.CreateCard(excStack);
        excCard.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(excCard);

        // 4. 系统集成与工具
        StackPanel sysStack = new();
        sysStack.Children.Add(DarkTheme.SectionHeader("系统集成与离线工具"));
        sysStack.Children.Add(_taskStatusText);

        WrapPanel toolsRow = new() { Margin = new Thickness(0, 12, 0, 0) };
        Button installTaskBtn = DarkTheme.CreateButton("运行安装/更新自启脚本", compact: true);
        installTaskBtn.Margin = new Thickness(0, 0, 8, 8);
        installTaskBtn.Click += (_, _) => RunInstallTaskScript();

        Button openDataBtn = DarkTheme.CreateButton("打开数据目录", compact: true);
        openDataBtn.Margin = new Thickness(0, 0, 8, 8);
        openDataBtn.Click += (_, _) => DataDirectoryRequested?.Invoke();

        Button exportReportBtn = DarkTheme.CreateButton("生成离线 HTML 看板", compact: true);
        exportReportBtn.Margin = new Thickness(0, 0, 8, 8);
        exportReportBtn.Click += (_, _) => ExportOfflineReport();

        Button exportDrillBtn = DarkTheme.CreateButton("生成离线双拼练习", compact: true);
        exportDrillBtn.Margin = new Thickness(0, 0, 8, 8);
        exportDrillBtn.Click += (_, _) => LaunchOfflineDrill(null);

        toolsRow.Children.Add(installTaskBtn);
        toolsRow.Children.Add(openDataBtn);
        toolsRow.Children.Add(exportReportBtn);
        toolsRow.Children.Add(exportDrillBtn);
        sysStack.Children.Add(toolsRow);
        sysStack.Children.Add(_settingsFeedbackText);

        root.Children.Add(DarkTheme.CreateCard(sysStack));
        return root;
    }

    private void WireEvents()
    {
        _todayRangeBtn.Click += async (_, _) => await SwitchRangeAsync(StatsRange.Today);
        _days7RangeBtn.Click += async (_, _) => await SwitchRangeAsync(StatsRange.Days7);
        _allRangeBtn.Click += async (_, _) => await SwitchRangeAsync(StatsRange.All);

        _modeSpBtn.Click += (_, _) => SwitchHeatmapMode(HeatmapDisplayMode.Shuangpin);
        _modeEnBtn.Click += (_, _) => SwitchHeatmapMode(HeatmapDisplayMode.English);
        _modeSymBtn.Click += (_, _) => SwitchHeatmapMode(HeatmapDisplayMode.Symbols);

        _heatmap.KeySelected += (metric, letter) =>
        {
            _selectedWeakChar = letter;
            if (letter is char ch)
            {
                string upper = char.ToUpperInvariant(ch).ToString();
                _selectedKeyInfoText.Text = metric is not null
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"已选中键位 {upper}：总击键 {metric.Presses:N0} · 确认纠错 {metric.Typos} ({metric.TypoRate * 100:0.0}%) · 中位 {metric.MedianIkiMs:0} ms")
                    : $"已选中键位 {upper}：当前时间范围内暂无有效样本。";
                _selectedKeyOfflineBtn.Visibility = Visibility.Visible;
                _selectedKeyWebBtn.Visibility = Visibility.Visible;
            }
        };

        _selectedKeyOfflineBtn.Click += (_, _) => LaunchOfflineDrill(_selectedWeakChar);
        _selectedKeyWebBtn.Click += (_, _) => OpenWebRequested?.Invoke(_config.WebPracticeUrl);

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

        _imeCombo.SelectionChanged += async (_, _) =>
        {
            if (_suppressSettingEvents || _imeCombo.SelectedIndex < 0 || _imeCombo.SelectedIndex >= ImeOptions.Length)
            {
                return;
            }

            string val = ImeOptions[_imeCombo.SelectedIndex].Value;
            _config.ImeOverride = val == "auto" ? null : val;
            SaveConfigAndNotify(_isViewerMode
                ? "已写入输入法方案配置（新事件将随采集进程生效）。"
                : "已切换输入法方案并向今日日志写入新的 meta 事件。");
            await ReloadStatsAsync();
        };
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
        DarkTheme.SetPillActive(_allRangeBtn, next == StatsRange.All);
        await ReloadStatsAsync();
    }

    private void SwitchHeatmapMode(HeatmapDisplayMode next)
    {
        if (_heatmapMode == next)
        {
            return;
        }

        _heatmapMode = next;
        _heatmap.DisplayMode = next;
        DarkTheme.SetPillActive(_modeSpBtn, next == HeatmapDisplayMode.Shuangpin);
        DarkTheme.SetPillActive(_modeEnBtn, next == HeatmapDisplayMode.English);
        DarkTheme.SetPillActive(_modeSymBtn, next == HeatmapDisplayMode.Symbols);
        if (_snapshot is not null)
        {
            RebuildWeakCards(_snapshot);
        }
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

        SaveConfigAndNotify(hide ? "已开启隐私遮罩：隐藏实时流和明细里的明文。" : "已关闭隐私遮罩。");
        RefreshLiveTick();
        if (_snapshot is not null)
        {
            RebuildDetailsFeeds(_snapshot);
        }
    }

    private void OnCaptureTextToggled(bool capture)
    {
        if (_suppressSettingEvents)
        {
            return;
        }

        _config.CaptureText = capture;
        SaveConfigAndNotify(_isViewerMode
            ? "已保存文本采集开关配置；独立只读视图下将在下次后台加载配置时生效。"
            : (capture ? "已开启 UIA 上屏文本采集。" : "已切为纯按键计数模式（停止读取上屏文本）。"));
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
            SetSettingsFeedback($"进程 {name} 已在排除名单中。");
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
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        foreach (string item in _config.ExtraExclusions)
        {
            string captured = item;
            StackPanel chipContent = new() { Orientation = Orientation.Horizontal };
            chipContent.Children.Add(new TextBlock
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
                Padding = new Thickness(4, 0, 2, 0),
            };
            removeBtn.Click += (_, _) =>
            {
                _config.ExtraExclusions.RemoveAll(s => string.Equals(s, captured, StringComparison.OrdinalIgnoreCase));
                RebuildCustomExclusionChips();
                SaveConfigAndNotify($"已移除自定义排除：{captured}");
            };
            chipContent.Children.Add(removeBtn);

            _customExclusionsPanel.Children.Add(new Border
            {
                Background = DarkTheme.ElevatedBrush,
                BorderBrush = DarkTheme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 8),
                Child = chipContent,
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
            _chartTitleText.Text = "今日分时段击键分布 (24 小时)";
            IReadOnlyList<HourlyPoint> hours = snap.HourlySeries;
            int maxKeys = Math.Max(1, hours.Max(h => h.Keystrokes));
            int peakHour = hours.OrderByDescending(h => h.Keystrokes).FirstOrDefault()?.Hour ?? 0;
            _chartMetaText.Text = snap.TodayKeystrokesFromDisk > 0
                ? string.Create(CultureInfo.InvariantCulture, $"今日峰值时段 {peakHour:00}:00（{maxKeys:N0} 键）")
                : "今日暂无击键数据";

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

        _chartTitleText.Text = _range == StatsRange.Days7 ? "近 7 天每日击键趋势" : "每日击键趋势 (近 14 天)";
        IReadOnlyList<DailyPoint> days = snap.DailySeries;
        if (days.Count == 0)
        {
            _chartMetaText.Text = "暂无每日统计";
            _chartGrid.Children.Add(EmptyNotice("当前范围没有可展示的每日日志"));
            return;
        }

        int maxDayKeys = Math.Max(1, days.Max(d => d.Keystrokes));
        _chartMetaText.Text = string.Create(CultureInfo.InvariantCulture, $"共 {days.Count} 天 · 单日最高 {maxDayKeys:N0} 键");

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
        Grid col = new() { Margin = new Thickness(3, 0, 3, 0), ToolTip = tooltip, Background = Brushes.Transparent };
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        const double maxBarPx = 116.0;
        double h = value <= 0 ? 3.0 : Math.Max(6.0, maxBarPx * value / max);
        Border bar = new()
        {
            Height = h,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            Background = value <= 0
                ? DarkTheme.ElevatedBrush
                : (hasTypos ? DarkTheme.GreenBrush : DarkTheme.BlueBrush),
            MaxWidth = 34,
        };
        Grid.SetRow(bar, 0);
        col.Children.Add(bar);

        TextBlock label = new()
        {
            Text = xLabel,
            FontSize = 12,
            Foreground = DarkTheme.DimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(label, 1);
        col.Children.Add(label);

        return col;
    }

    private void SeedTelemetryFeedFromSnapshot(DesktopSnapshot snap)
    {
        List<LiveFeedItem> seeded = [];
        foreach (DesktopCommitItem c in snap.RecentCommits.Take(12))
        {
            seeded.Add(new LiveFeedItem(c.Ts, c.Exe, "commit", c.Text, c.Keys, null, null));
        }

        foreach (TypoRecord t in snap.RecentTypos.Take(10))
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
            _liveFeedPanel.Children.Add(EmptyNotice("等待击键与上屏事件... 在任意软件里打字，这里会实时滚出上屏文本和退格纠错。"));
            return;
        }

        foreach (LiveFeedItem item in live.Feed.Take(18))
        {
            bool isCorrection = item.Kind == "correction";
            string timeText = DateTimeOffset.FromUnixTimeMilliseconds(item.Ts).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            Grid rowGrid = new();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
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
                Foreground = isCorrection ? DarkTheme.TextBrush : (_config.HideLiveText ? DarkTheme.FaintBrush : DarkTheme.TextBrush),
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
                Padding = new Thickness(4, 7, 4, 7),
                Child = rowGrid,
            });
        }
    }

    private void RebuildWeakCards(DesktopSnapshot snap)
    {
        _weakCardsGrid.Children.Clear();

        if (_heatmapMode == HeatmapDisplayMode.Symbols)
        {
            _weakSectionTitle.Text = "日常常用/易错编程符号统计 (Top 12)";
            if (snap.WeakSymbols.Count == 0)
            {
                _weakCardsGrid.Columns = 1;
                _weakCardsGrid.Children.Add(DarkTheme.CreateCard(EmptyNotice("当前范围暂无符号击键统计")));
                return;
            }

            _weakCardsGrid.Columns = 3;
            foreach (WeakSymbolMetric sym in snap.WeakSymbols)
            {
                StackPanel stack = new();
                DockPanel top = new() { LastChildFill = false };
                Border tile = new()
                {
                    Width = 36,
                    Height = 36,
                    Background = DarkTheme.ElevatedBrush,
                    BorderBrush = DarkTheme.BorderStrongBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = new TextBlock
                    {
                        Text = sym.Char.ToString(),
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        FontFamily = DarkTheme.MonoFont,
                        Foreground = DarkTheme.TextBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                StackPanel info = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = string.Create(CultureInfo.InvariantCulture, $"错率 {sym.TypoRate * 100:0.0}%"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = sym.Typos > 0 ? DarkTheme.RedBrush : DarkTheme.TextBrush,
                });
                info.Children.Add(new TextBlock
                {
                    Text = string.Create(CultureInfo.InvariantCulture, $"{sym.Typos} / {sym.Presses} 次"),
                    FontSize = 12,
                    Foreground = DarkTheme.DimBrush,
                });

                DockPanel.SetDock(tile, Dock.Left);
                top.Children.Add(tile);
                top.Children.Add(info);
                stack.Children.Add(top);

                Border card = DarkTheme.CreateCard(stack, new Thickness(12));
                card.Margin = new Thickness(0, 0, 10, 10);
                _weakCardsGrid.Children.Add(card);
            }

            return;
        }

        _weakSectionTitle.Text = "日常最容易错 / 最慢的键 (Top 12)";
        if (snap.WeakLetters.Count == 0)
        {
            _weakCardsGrid.Columns = 1;
            _weakCardsGrid.Children.Add(DarkTheme.CreateCard(EmptyNotice("当前范围还没有足够的错按样本，继续自然输入即可自动识别弱键。")));
            return;
        }

        _weakCardsGrid.Columns = 3;
        foreach (KeyCellMetric key in snap.WeakLetters)
        {
            char lower = char.ToLowerInvariant(key.Name[0]);
            StackPanel cardStack = new();

            // Row 1: Tile + Primary stats + Compact drill button
            Grid topRow = new();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border tile = new()
            {
                Width = 36,
                Height = 36,
                Background = DarkTheme.ElevatedBrush,
                BorderBrush = DarkTheme.BorderStrongBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock
                {
                    Text = key.Name,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = DarkTheme.TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(tile, 0);

            StackPanel primary = new() { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            primary.Children.Add(new TextBlock
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"错率 {key.TypoRate * 100:0.0}%"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = DarkTheme.RedBrush,
            });
            primary.Children.Add(new TextBlock
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"{key.Typos} / {key.Presses} 键"),
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
            });
            Grid.SetColumn(primary, 1);

            Button drillBtn = DarkTheme.CreateButton("离线练", primary: false, compact: true);
            drillBtn.Click += (_, _) => LaunchOfflineDrill(lower);
            Grid.SetColumn(drillBtn, 2);

            topRow.Children.Add(tile);
            topRow.Children.Add(primary);
            topRow.Children.Add(drillBtn);
            cardStack.Children.Add(topRow);

            // Row 2: Median IKI & Equivalent WPM
            DockPanel bottomRow = new() { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
            TextBlock ikiText = new()
            {
                Text = key.MedianIkiMs > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"中位 {key.MedianIkiMs:0} ms")
                    : "中位 -- ms",
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
            };
            TextBlock wpmText = new()
            {
                Text = key.Wpm > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"等效 {key.Wpm:0.0} wpm")
                    : "等效 -- wpm",
                FontSize = 12,
                Foreground = DarkTheme.DimBrush,
            };
            DockPanel.SetDock(ikiText, Dock.Left);
            DockPanel.SetDock(wpmText, Dock.Right);
            bottomRow.Children.Add(ikiText);
            bottomRow.Children.Add(wpmText);
            cardStack.Children.Add(bottomRow);

            Border card = DarkTheme.CreateCard(cardStack, new Thickness(12));
            card.Margin = new Thickness(0, 0, 10, 10);
            _weakCardsGrid.Children.Add(card);
        }
    }

    private void RebuildAppsTable(DesktopSnapshot snap)
    {
        _appsTablePanel.Children.Clear();
        if (snap.Apps.Count == 0)
        {
            _appsTablePanel.Children.Add(EmptyNotice("当前范围暂无分应用统计"));
            return;
        }

        _appsTablePanel.Children.Add(BuildAppTableRow("应用", "总击键", "上屏数", "退格错", "准确率", "文本读取", isHeader: true));

        foreach (DesktopAppMetric app in snap.Apps.Take(12))
        {
            _appsTablePanel.Children.Add(BuildAppTableRow(
                app.Exe,
                app.Keystrokes.ToString("N0", CultureInfo.InvariantCulture),
                app.Commits.ToString("N0", CultureInfo.InvariantCulture),
                app.Typos.ToString("N0", CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{app.AccuracyPct:0.0}%"),
                app.TextAvailable ? "可读取" : "盲区(只计键)",
                isHeader: false,
                textAvailable: app.TextAvailable));
        }
    }

    private static FrameworkElement BuildAppTableRow(
        string c1,
        string c2,
        string c3,
        string c4,
        string c5,
        string c6,
        bool isHeader,
        bool textAvailable = true)
    {
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        Brush fg = isHeader ? DarkTheme.DimBrush : DarkTheme.TextBrush;
        FontWeight fw = isHeader ? FontWeights.SemiBold : FontWeights.Normal;

        TextBlock MakeCell(string text, int col, TextAlignment align, Brush? color = null)
        {
            TextBlock tb = new()
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = fw,
                Foreground = color ?? fg,
                TextAlignment = align,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(tb, col);
            return tb;
        }

        row.Children.Add(MakeCell(c1, 0, TextAlignment.Left));
        row.Children.Add(MakeCell(c2, 1, TextAlignment.Right));
        row.Children.Add(MakeCell(c3, 2, TextAlignment.Right));
        row.Children.Add(MakeCell(c4, 3, TextAlignment.Right));
        row.Children.Add(MakeCell(c5, 4, TextAlignment.Right));
        row.Children.Add(MakeCell(c6, 5, TextAlignment.Right, isHeader ? DarkTheme.DimBrush : (textAvailable ? DarkTheme.GreenBrush : DarkTheme.FaintBrush)));

        return new Border
        {
            BorderBrush = DarkTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, isHeader ? 6 : 8, 6, isHeader ? 8 : 8),
            Child = row,
        };
    }

    private void RebuildDetailsFeeds(DesktopSnapshot snap)
    {
        _typosFeedPanel.Children.Clear();
        if (snap.RecentTypos.Count == 0)
        {
            _typosFeedPanel.Children.Add(EmptyNotice("暂无真实纠错还原记录"));
        }
        else
        {
            foreach (TypoRecord t in snap.RecentTypos.Take(16))
            {
                string timeText = DateTimeOffset.FromUnixTimeMilliseconds(t.Ts).ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string del = t.Deleted is char d ? d.ToString().ToUpperInvariant() : "?";
                string rep = t.Replacement is char r ? r.ToString().ToUpperInvariant() : "?";
                string causeLabel = t.Cause switch
                {
                    "pinyin" => "拼音层",
                    "adjacent" => "相邻误按",
                    _ => "句内纠错",
                };

                StackPanel item = new();
                DockPanel head = new() { LastChildFill = false };
                head.Children.Add(new TextBlock
                {
                    Text = $"{timeText} · {t.Exe}",
                    FontSize = 12,
                    Foreground = DarkTheme.DimBrush,
                });
                TextBlock badge = new()
                {
                    Text = causeLabel,
                    FontSize = 12,
                    Foreground = DarkTheme.BlueBrush,
                };
                DockPanel.SetDock(badge, Dock.Right);
                head.Children.Add(badge);
                item.Children.Add(head);

                string ctx = _config.HideLiveText || string.IsNullOrWhiteSpace(t.Word)
                    ? ""
                    : $" · 「{t.Word}」";
                item.Children.Add(new TextBlock
                {
                    Text = $"误按 {del} → 改按 {rep}{ctx}",
                    FontSize = 12.5,
                    Foreground = DarkTheme.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });

                _typosFeedPanel.Children.Add(new Border
                {
                    BorderBrush = DarkTheme.BorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(2, 8, 2, 8),
                    Child = item,
                });
            }
        }

        _commitsFeedPanel.Children.Clear();
        if (snap.RecentCommits.Count == 0)
        {
            _commitsFeedPanel.Children.Add(EmptyNotice("暂无录到的上屏片段"));
        }
        else
        {
            foreach (DesktopCommitItem c in snap.RecentCommits.Take(16))
            {
                string timeText = DateTimeOffset.FromUnixTimeMilliseconds(c.Ts).ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                StackPanel item = new();
                DockPanel head = new() { LastChildFill = false };
                head.Children.Add(new TextBlock
                {
                    Text = $"{timeText} · {c.Exe}",
                    FontSize = 12,
                    Foreground = DarkTheme.DimBrush,
                });
                TextBlock keysLabel = new()
                {
                    Text = string.Create(CultureInfo.InvariantCulture, $"{c.Keys} 键"),
                    FontSize = 12,
                    Foreground = DarkTheme.DimBrush,
                };
                DockPanel.SetDock(keysLabel, Dock.Right);
                head.Children.Add(keysLabel);
                item.Children.Add(head);

                string text = _config.HideLiveText
                    ? string.Create(CultureInfo.InvariantCulture, $"[已隐藏明文 · {c.Text.Length} 字]")
                    : FormatSingleLinePreview(c.Text);
                item.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    Foreground = _config.HideLiveText ? DarkTheme.FaintBrush : DarkTheme.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });

                _commitsFeedPanel.Children.Add(new Border
                {
                    BorderBrush = DarkTheme.BorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(2, 8, 2, 8),
                    Child = item,
                });
            }
        }
    }

    private void LaunchOfflineDrill(char? focusChar)
    {
        try
        {
            HashSet<char> weakSet = [];
            if (focusChar is char ch && char.IsAsciiLetterLower(ch))
            {
                weakSet.Add(ch);
            }
            else if (_snapshot is not null)
            {
                foreach (KeyCellMetric metric in _snapshot.WeakLetters.Take(5))
                {
                    weakSet.Add(char.ToLowerInvariant(metric.Name[0]));
                }
            }

            if (weakSet.Count == 0)
            {
                weakSet.UnionWith(['u', 'v', 'i', 'c', 'h']);
            }

            List<HanziEntry> picked = ZhSelector.Pick(weakSet, 60);
            if (picked.Count == 0)
            {
                SetSettingsFeedback("内置汉字表为空，无法生成离线练习。", isError: true);
                return;
            }

            string itemsJson = Program.BuildDrillItemsJson(picked);
            string label = string.Join(", ", weakSet.Select(char.ToUpperInvariant));
            string title = $"{picked.Count} 个字 · {label} 专项练习";
            string outPath = System.IO.Path.Combine(_baseDir, "drill.html");
            DrillPage.Write(outPath, itemsJson, title);

            Process.Start(new ProcessStartInfo { FileName = outPath, UseShellExecute = true });
            SetSettingsFeedback($"已生成并打开离线练习：{outPath}");
        }
        catch (Exception ex)
        {
            SetSettingsFeedback($"生成离线练习失败：{ex.Message}", isError: true);
        }
    }

    private async void ExportOfflineReport()
    {
        try
        {
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly? since = today.AddDays(-6);
            string statsPath = System.IO.Path.Combine(_baseDir, "stats.json");
            string reportPath = System.IO.Path.Combine(_baseDir, "report.html");

            await Task.Run(() =>
            {
                (List<string> files, IEnumerable<EventRecord> stream, int rejected) = Analysis.EventLog.Read(_eventsDir, since, today);
                List<EventRecord> events = [.. stream];
                AttributorOptions opt = new();
                AttributionResult r = new Attributor(opt).Run(events, rejected);
                StatsContext ctx = new()
                {
                    Directory = _eventsDir,
                    Files = files,
                    Events = events.Count,
                    From = since ?? today,
                    To = today,
                    PauseFactor = opt.PauseFactor,
                    TypoMaxBackspaces = opt.TypoMaxBackspaces,
                    MinKeySamples = opt.MinKeySamples,
                };
                StatsBuilder.WriteJson(statsPath, r, ctx);
                Dashboard.Write(reportPath, File.ReadAllText(statsPath));
            });

            Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
            SetSettingsFeedback($"已导出并打开离线报告：{reportPath}");
        }
        catch (Exception ex)
        {
            SetSettingsFeedback($"导出离线报告失败：{ex.Message}", isError: true);
        }
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
            SetSettingsFeedback("找不到 scripts\\install-task.bat，请到源码仓库运行。", isError: true);
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
            SetSettingsFeedback("已调起 install-task.bat 安装/更新计划任务。");
        }
        catch (Exception ex)
        {
            SetSettingsFeedback($"启动脚本失败：{ex.Message}", isError: true);
        }
    }

    private async Task RefreshScheduledTaskStatusAsync()
    {
        string status = await Task.Run(() =>
        {
            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Query /TN \"KeyTrace\" /FO LIST",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process? p = Process.Start(psi);
                if (p is null)
                {
                    return "无法查询计划任务状态。";
                }

                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (p.ExitCode != 0)
                {
                    return "开机高权限计划任务 (KeyTrace)：未注册（可点击下方脚本按钮安装）。";
                }

                bool running = output.Contains("Running", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("正在运行", StringComparison.OrdinalIgnoreCase);
                return running
                    ? "开机高权限计划任务 (KeyTrace)：已注册，当前处于运行中。"
                    : "开机高权限计划任务 (KeyTrace)：已注册（就绪状态）。";
            }
            catch (Exception ex)
            {
                return $"查询计划任务失败：{ex.Message}";
            }
        });

        _taskStatusText.Text = status;
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
        FontSize = 23,
        FontWeight = FontWeights.SemiBold,
        Foreground = DarkTheme.TextBrush,
        Margin = new Thickness(0, 4, 0, 3),
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
            Width = 12,
            Height = 10,
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

    private static string FormatImeLabel(string ime) => ime switch
    {
        "shuangpin/xiaohe" => "小鹤双拼",
        "shuangpin/ziranma" => "自然码双拼",
        "shuangpin/microsoft" => "微软双拼",
        "shuangpin/sogou" => "搜狗双拼",
        "full-pinyin" => "全拼",
        _ => ime,
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        if (bytes < 1024 * 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.00} MB");
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
