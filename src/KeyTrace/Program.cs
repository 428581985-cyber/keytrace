using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using KeyTrace.Analysis;
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

            case "stats":
                return BuildStats(args[1..]);

            case "label":
                return ExportLabels(args[1..]);

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
              stats      把 events.jsonl 归因成 stats.json 与 typos.jsonl
              label      导出金标标注表，人工打勾后用来校准判定规则
              pause      暂停录制（等同托盘菜单 / Ctrl+Alt+P）
              resume     继续录制
              stop       让常驻进程退出

            选项：
              --out <目录>   数据目录，默认 %USERPROFILE%\.keytrace\events
              -h, --help     显示本帮助
              --version      显示版本

            stats 专用：
              --in <目录>        事件目录，默认同上
              --out <文件>       统计文件，默认 %USERPROFILE%\.keytrace\stats.json
              --typos <文件>     逐条明细，默认与统计文件同目录的 typos.jsonl
              --since <范围>     7d / 全部写 all / 起始日期 2026-11-01，默认 7d
              --pause-factor <n> 停顿门槛 = IKI 中位数 × n，默认 3
              --min-samples <n>  单键样本门槛，默认 20

            label 专用：
              --out <文件>       标注表，默认 %USERPROFILE%\.keytrace\labeling-日期.csv
              --total <n>        共导出多少条，默认 200
              --seed <n>         抽样种子，默认 20261117（同种子导两次结果一样）
              --anonymize        去掉文字、应用、时间，只留结构特征（准备公开时用）

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

    /// <summary>归因：读事件目录，写 stats.json + typos.jsonl。</summary>
    private static int BuildStats(string[] args)
    {
        string directory = Value(args, "--in") ?? DefaultDataDirectory();
        string root = Path.GetDirectoryName(Path.GetFullPath(directory))!;

        string output = Value(args, "--out") ?? Path.Combine(root, "stats.json");
        if (Directory.Exists(output))
        {
            output = Path.Combine(output, "stats.json");
        }

        string typos = Value(args, "--typos")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "typos.jsonl");

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (!TryParseSince(Value(args, "--since") ?? "7d", today, out DateOnly? since))
        {
            Console.Error.WriteLine("--since 只接受 7d、all 或 2026-11-01 这样的日期。");
            return 1;
        }

        double pauseFactor = 3.0;
        if (Value(args, "--pause-factor") is string factorText
            && !double.TryParse(factorText, NumberStyles.Float, CultureInfo.InvariantCulture, out pauseFactor))
        {
            Console.Error.WriteLine("--pause-factor 需要一个小数，例如 3。");
            return 1;
        }

        int minSamples = 20;
        if (Value(args, "--min-samples") is string sampleText
            && !int.TryParse(sampleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out minSamples))
        {
            Console.Error.WriteLine("--min-samples 需要一个整数，例如 20。");
            return 1;
        }

        (List<string> files, IEnumerable<EventRecord> stream, int rejected) = EventLog.Read(directory, since, today);
        List<EventRecord> events = [.. stream];

        if (events.Count == 0)
        {
            Console.Error.WriteLine($"没有可读的事件：{directory}（范围 {since?.ToString("yyyy-MM-dd") ?? "全部"} 起）。");
            return 1;
        }

        AttributorOptions options = new()
        {
            PauseFactor = pauseFactor,
            MinKeySamples = minSamples,
        };

        AttributionResult result = new Attributor(options).Run(events, rejected);

        StatsContext context = new()
        {
            Directory = directory,
            Files = files,
            Events = events.Count,
            From = since ?? EarliestFileDate(files) ?? today,
            To = today,
            PauseFactor = pauseFactor,
            TypoMaxBackspaces = options.TypoMaxBackspaces,
            MinKeySamples = minSamples,
        };

        StatsBuilder.WriteJson(output, result, context);
        StatsBuilder.WriteTypos(typos, result);

        Console.WriteLine($"事件 {events.Count} 条（{files.Count} 个文件，跳过 {result.Skipped.Total + rejected} 条）");
        Console.WriteLine($"击键 {result.Totals.Keystrokes}，上屏 {result.Totals.Chars} 字，会话 {result.Totals.Sessions}");
        Console.WriteLine($"打错 {result.Totals.Typos}（拼音层 {result.Totals.PinyinErrors}），选字错 {result.Totals.SelectionErrors}，改措辞 {result.Totals.Rewording}");
        Console.WriteLine($"中文 {result.Chinese.Commits} 条上屏，带组字中间态 {CompCoverage(result)}");
        Console.WriteLine();
        Console.WriteLine($"→ {output}");
        Console.WriteLine($"→ {typos}");

        return 0;
    }

    /// <summary>导出金标标注表：判错的全给，没判错的抽样给，用来同时算 precision 和 recall。</summary>
    private static int ExportLabels(string[] args)
    {
        ConsoleBridge.Attach();

        string directory = Value(args, "--in") ?? DefaultDataDirectory();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        if (!TryParseSince(Value(args, "--since") ?? "7d", today, out DateOnly? since))
        {
            Console.Error.WriteLine("--since 只接受 7d、all 或 2026-11-01 这样的日期。");
            return 1;
        }

        int total = 200;
        if (Value(args, "--total") is string totalText && (!int.TryParse(totalText, out total) || total < 1))
        {
            Console.Error.WriteLine("--total 需要正整数，例如 200。");
            return 1;
        }

        int seed = 20261117;
        if (Value(args, "--seed") is string seedText && !int.TryParse(seedText, out seed))
        {
            Console.Error.WriteLine("--seed 需要整数。");
            return 1;
        }

        bool anonymize = HasFlag(args, "--anonymize");
        string output = Value(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            $"labeling-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv");

        if (Directory.Exists(output))
        {
            output = Path.Combine(output, "labeling.csv");
        }

        (List<string> files, IEnumerable<EventRecord> stream, int rejected) = EventLog.Read(directory, since, today);
        List<EventRecord> events = [.. stream];
        if (events.Count == 0)
        {
            Console.Error.WriteLine($"没有可读的事件：{directory}");
            return 1;
        }

        AttributionResult result = new Attributor(new AttributorOptions { RecordAll = true }).Run(events, rejected);
        List<LabelRow> rows = LabelExporter.Build(result, total, seed, anonymize);
        string keyPath = LabelExporter.Write(output, rows);

        int errors = rows.Count(r => r.Predicted);
        int sampled = rows.Count - errors;
        double rate = LabelExporter.SampleRate(result, total);

        Console.WriteLine($"事件 {events.Count} 条（{files.Count} 个文件）");
        Console.WriteLine($"标注表 {rows.Count} 条：判错的 {errors} 条全给 + 没判错的抽样 {sampled} 条");
        Console.WriteLine($"抽样率 {rate * 100:0.0}%（估 recall 时按这个加权）");
        if (anonymize)
        {
            Console.WriteLine("已去标识：文字、应用、时间都已清空，只留结构特征。");
        }

        Console.WriteLine();
        Console.WriteLine($"标注表 → {output}");
        Console.WriteLine($"机器判定 → {keyPath}（别打开，会污染你的判断）");
        Console.WriteLine();
        Console.WriteLine("在「你的判断」列填 1（这是打错）或 0（不是），保存后把标注表发我。");

        return 0;
    }

    private static string CompCoverage(AttributionResult r) =>
        r.Chinese.Commits == 0
            ? "（无中文）"
            : $"{r.Chinese.CommitsWithComp}/{r.Chinese.Commits}（{100.0 * r.Chinese.CommitsWithComp / r.Chinese.Commits:0}%）";

    private static DateOnly? EarliestFileDate(List<string> files)
    {
        DateOnly? earliest = null;
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            string stem = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(name))
                : Path.GetFileNameWithoutExtension(name);

            if (DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                && (earliest is null || day < earliest))
            {
                earliest = day;
            }
        }

        return earliest;
    }

    private static bool TryParseSince(string text, DateOnly today, out DateOnly? since)
    {
        since = null;
        text = text.Trim();

        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.EndsWith('d') && int.TryParse(text[..^1], out int days) && days > 0)
        {
            since = today.AddDays(1 - days);
            return true;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            since = day;
            return true;
        }

        return false;
    }

    private static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
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

        return DefaultDataDirectory();
    }

    private static string DefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".keytrace",
        "events");
}
