using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using KeyTrace.Collect;
using KeyTrace.Control;
using KeyTrace.Ui;

namespace KeyTrace;

internal static class Program
{
    private const string Version = "0.0.1";

    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleBridge.Attach();

        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        switch (args[0])
        {
            case "-h":
            case "--help":
                PrintHelp();
                return 0;

            case "--version":
                Console.WriteLine($"keytrace {Version}");
                return 0;

            case "run":
                return RunDaemon(args[1..]);

            case "collect":
                return CollectForeground(args[1..]);

            case "pause":
                return Report(ControlSignals.SignalPause(), "已暂停", "没有正在运行的 KeyTrace。");

            case "resume":
                return Report(ControlSignals.SignalResume(), "已继续", "没有正在运行的 KeyTrace。");

            case "stop":
                return Report(ControlSignals.SignalStop(), "已请求停止。", "没有正在运行的 KeyTrace。");

            default:
                Console.Error.WriteLine($"未知命令：{args[0]}");
                Console.Error.WriteLine("运行 keytrace --help 查看可用命令。");
                return 1;
        }
    }

    private static int Report(bool ok, string success, string failure)
    {
        if (ok)
        {
            Console.WriteLine(success);
            return 0;
        }

        Console.Error.WriteLine(failure);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            keytrace —— 本地打字数据采集与练习材料生成

            用法：
              keytrace <命令> [选项]

            命令：
              run        常驻录制，带托盘图标（推荐日常使用）
              collect    前台录制，调试用，Ctrl+C 结束
              pause      暂停录制（等同托盘菜单 / Ctrl+Alt+P）
              resume     继续录制
              stop       让常驻进程退出

            选项：
              --out <目录>   数据目录，默认 %USERPROFILE%\.keytrace\events
              -h, --help     显示本帮助
              --version      显示版本

            全部数据只存本地，不联网。
            """);
    }

    /// <summary>常驻模式：单实例 + 托盘 + 热键 + 采集，靠 Application.Run 泵消息。</summary>
    private static int RunDaemon(string[] args)
    {
        string target = ResolveDataDirectory(args);

        using Mutex? instance = ControlSignals.TryAcquireInstance();
        if (instance is null)
        {
            Console.Error.WriteLine("已经有一个 KeyTrace 在运行了。");
            return 1;
        }

        (EventWaitHandle stopSignal, EventWaitHandle pauseSignal) = ControlSignals.CreateServerSide();
        using EventWaitHandle stop = stopSignal;
        using EventWaitHandle pause = pauseSignal;

        using EventWriter writer = new(target);
        using RawInputCollector collector = new(
            writer, stop, pause,
            captureText: !HasFlag(args, "--no-text"),
            traceText: HasFlag(args, "--debug-text"));
        TrayApplication tray = new(target);

        bool exiting = false;

        void Shutdown()
        {
            if (exiting)
            {
                return;
            }

            exiting = true;
            tray.Shutdown();
            Application.ExitThread();
        }

        tray.ToggleRequested += collector.TogglePause;
        tray.ExitRequested += () =>
        {
            collector.RequestStop();
            Shutdown();
        };

        collector.PauseChanged += tray.SetPaused;
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
            tray.Notify($"热键 {GlobalHotkey.Description} 被其他程序占用，请用托盘菜单暂停。");
        }

        Application.Run(tray);

        writer.Flush();
        return 0;
    }

    /// <summary>调试模式：前台跑，Ctrl+C 或 stop 都能结束。</summary>
    private static int CollectForeground(string[] args)
    {
        string target = ResolveDataDirectory(args);

        using EventWriter writer = new(target);
        using RawInputCollector collector = new(writer, captureText: !HasFlag(args, "--no-text"));

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // 不要被 Ctrl+C 直接杀掉，让消息循环自然收尾并落盘
            collector.RequestStop();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            Console.WriteLine($"录制中 → {target}");
            Console.WriteLine("按 Ctrl+C 结束。");
            Console.WriteLine();

            collector.Run();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            writer.Flush();
        }

        Console.WriteLine();
        Console.WriteLine($"已停止。共记录 {collector.EventCount} 个事件。");
        return 0;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string arg in args)
        {
            if (arg == flag)
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveDataDirectory(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "events");
    }
}
