using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KeyTrace.Analysis;
using KeyTrace.Collect;

namespace KeyTrace.Ui;

/// <summary>
/// 统筹 WPF 消息循环、托盘图标、桌面主窗口与可选迷你悬浮条。
/// 支持完整常驻模式（配合 RawInputCollector）与独立只读视图模式（RunViewer）。
/// </summary>
internal static class DesktopAppHost
{
    public static int RunDaemon(
        RawInputCollector collector,
        EventWriter writer,
        UserConfig config,
        string baseDir,
        bool showOnStart)
    {
        Application app = EnsureWpfApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        LiveTelemetry telemetry = new();
        collector.EventRecorded += telemetry.Observe;

        bool showWebPractice = ShouldShowWebPractice(config);
        TrayApplication tray = new(config.ShowMiniHud, showWebPractice);
        MainWindow? mainWindow = null;
        MiniHudWindow? miniHud = null;
        bool exiting = false;
        int dashboardBusy = 0;

        DispatcherTimer hudTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        void OpenDashboardAsync()
        {
            if (exiting || Interlocked.Exchange(ref dashboardBusy, 1) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    OpenStandaloneDashboard(
                        collector.DataDirectory,
                        baseDir,
                        writer.Flush,
                        path => OpenUrl(path, tray.Notify),
                        tray.Notify);
                }
                finally
                {
                    Interlocked.Exchange(ref dashboardBusy, 0);
                }
            });
        }

        void SyncMiniHudVisual()
        {
            if (miniHud is not null && miniHud.IsVisible)
            {
                miniHud.UpdateState(telemetry.Snapshot(), collector.IsPaused);
            }
        }

        void SetMiniHudVisible(bool visible)
        {
            config.ShowMiniHud = visible;
            SaveConfigSafe(config, baseDir);
            tray.SetMiniHudVisible(visible);

            if (visible)
            {
                if (miniHud is null)
                {
                    miniHud = CreateMiniHud(config, baseDir, canTogglePause: true, OpenMainWindow, collector.TogglePause, () => SetMiniHudVisible(false));
                }

                miniHud.UpdateState(telemetry.Snapshot(), collector.IsPaused);
                miniHud.Show();
            }
            else if (miniHud is not null)
            {
                miniHud.Close();
                miniHud = null;
            }
        }

        void OpenMainWindow()
        {
            if (exiting)
            {
                return;
            }

            if (mainWindow is null)
            {
                mainWindow = new MainWindow(
                    collector.DataDirectory,
                    telemetry,
                    isViewerMode: false,
                    isPausedInitial: collector.IsPaused);

                mainWindow.TogglePauseRequested += collector.TogglePause;
                mainWindow.OpenDashboardRequested += OpenDashboardAsync;
            }

            mainWindow.SetPaused(collector.IsPaused);
            mainWindow.BringToFront();
        }

        void Shutdown()
        {
            if (exiting)
            {
                return;
            }

            exiting = true;
            hudTimer.Stop();

            if (miniHud is not null)
            {
                miniHud.Close();
                miniHud = null;
            }

            if (mainWindow is not null)
            {
                mainWindow.AllowClose = true;
                mainWindow.Close();
                mainWindow = null;
            }

            tray.Shutdown();
            app.Shutdown();
        }

        hudTimer.Tick += (_, _) =>
        {
            tray.SetEventCount(collector.EventCount);
            SyncMiniHudVisual();
        };

        tray.OpenWindowRequested += OpenMainWindow;
        tray.OpenDashboardRequested += OpenDashboardAsync;
        tray.ToggleMiniHudRequested += () => SetMiniHudVisible(!config.ShowMiniHud);
        tray.TogglePauseRequested += collector.TogglePause;
        if (showWebPractice)
        {
            tray.OpenWebPracticeRequested += () =>
            {
                if (!string.IsNullOrWhiteSpace(config.WebPracticeUrl))
                {
                    OpenUrl(config.WebPracticeUrl, tray.Notify);
                }
            };
        }

        tray.ExitRequested += () =>
        {
            collector.RequestStop();
            Shutdown();
        };

        collector.PauseChanged += paused =>
        {
            tray.SetPaused(paused);
            mainWindow?.SetPaused(paused);
            SyncMiniHudVisual();
        };
        collector.ShowRequested += OpenMainWindow;
        collector.Stopped += Shutdown;

        try
        {
            collector.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            tray.Shutdown();
            return 1;
        }

        if (!collector.HotkeyRegistered)
        {
            tray.Notify($"热键 {Control.GlobalHotkey.Description} 被其他程序占用，请用托盘菜单暂停。");
        }

        if (config.ShowMiniHud)
        {
            SetMiniHudVisible(true);
        }

        if (showOnStart)
        {
            OpenMainWindow();
        }

        hudTimer.Start();
        app.Run();
        writer.Flush();
        return 0;
    }

    public static int RunViewer(string eventsDir, string baseDir, UserConfig config)
    {
        Application app = EnsureWpfApp();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;

        LiveTelemetry telemetry = new();
        MiniHudWindow? miniHud = null;
        MainWindow mainWindow = new(
            eventsDir,
            telemetry,
            isViewerMode: true,
            isPausedInitial: false)
        {
            AllowClose = true,
        };

        int dashboardBusy = 0;
        mainWindow.OpenDashboardRequested += () =>
        {
            if (Interlocked.Exchange(ref dashboardBusy, 1) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    OpenStandaloneDashboard(
                        eventsDir,
                        baseDir,
                        flushEvents: null,
                        path => OpenUrl(path, notify: null),
                        notify: null);
                }
                finally
                {
                    Interlocked.Exchange(ref dashboardBusy, 0);
                }
            });
        };

        DispatcherTimer hudTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        void SetMiniHudVisible(bool visible)
        {
            config.ShowMiniHud = visible;
            SaveConfigSafe(config, baseDir);

            if (visible)
            {
                if (miniHud is null)
                {
                    miniHud = CreateMiniHud(config, baseDir, canTogglePause: false, mainWindow.BringToFront, null, () => SetMiniHudVisible(false));
                }

                miniHud.UpdateState(telemetry.Snapshot(), paused: false);
                miniHud.Show();
            }
            else if (miniHud is not null)
            {
                miniHud.Close();
                miniHud = null;
            }
        }

        mainWindow.Closed += (_, _) =>
        {
            hudTimer.Stop();
            miniHud?.Close();
            miniHud = null;
        };

        hudTimer.Tick += (_, _) =>
        {
            if (miniHud is not null && miniHud.IsVisible)
            {
                if (!mainWindow.IsVisible)
                {
                    telemetry.PollTodayFile(eventsDir);
                }

                miniHud.UpdateState(telemetry.Snapshot(), paused: false);
            }
        };

        if (config.ShowMiniHud)
        {
            SetMiniHudVisible(true);
        }

        hudTimer.Start();
        app.Run(mainWindow);
        return 0;
    }

    private static Application EnsureWpfApp() => Application.Current ?? new Application();

    private static MiniHudWindow CreateMiniHud(
        UserConfig config,
        string baseDir,
        bool canTogglePause,
        Action openMain,
        Action? togglePause,
        Action hideHud)
    {
        MiniHudWindow hud = new(config.MiniHudLeft, config.MiniHudTop, canTogglePause);
        hud.OpenMainWindowRequested += openMain;
        if (togglePause is not null)
        {
            hud.TogglePauseRequested += togglePause;
        }

        hud.HideRequested += hideHud;
        hud.PositionChanged += (left, top) =>
        {
            config.MiniHudLeft = left;
            config.MiniHudTop = top;
            SaveConfigSafe(config, baseDir);
        };
        return hud;
    }

    private static void SaveConfigSafe(UserConfig config, string baseDir)
    {
        try
        {
            config.Save(baseDir);
        }
        catch (Exception)
        {
            // 配置保存失败不应打断主流程
        }
    }

    internal static bool ShouldShowWebPractice(UserConfig config) => config.HasWebPracticeUrl;

    internal static DashboardGenerationResult OpenStandaloneDashboard(
        string eventsDir,
        string baseDir,
        Action? flushEvents,
        Action<string> openPath,
        Action<string>? notify,
        int days = Dashboard.DefaultLookbackDays,
        DateOnly? todayOverride = null)
    {
        try
        {
            flushEvents?.Invoke();
        }
        catch (Exception)
        {
            // 落盘刷新异常不应阻断已写入日志的看板生成
        }

        DashboardGenerationResult result = Dashboard.GenerateFromEvents(eventsDir, baseDir, days, todayOverride);
        if (!result.Success || string.IsNullOrEmpty(result.ReportPath))
        {
            notify?.Invoke($"生成统计看板失败：{result.ErrorMessage ?? "未知错误"}");
            return result;
        }

        if (result.EventCount == 0)
        {
            notify?.Invoke("暂无打字记录，已打开空统计看板。正常打字一段时间后再查看即可看到完整统计。");
        }

        openPath(result.ReportPath);
        return result;
    }

    private static void OpenUrl(string url, Action<string>? notify)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            notify?.Invoke($"打不开页面：{ex.Message}");
        }
    }
}
