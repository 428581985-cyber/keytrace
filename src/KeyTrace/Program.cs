using System;
using System.Globalization;
using System.IO;
using System.Threading;
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
            if (!ConsoleBridge.Attached)
            {
                return RunUi([]);
            }

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

            case "ui":
                return RunUi(args[1..]);

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

            case "export":
                return ExportData(args[1..]);

            case "words":
                return BuildWords(args[1..]);

            case "dashboard":
                return BuildDashboard(args[1..]);

            case "drill":
                return BuildDrill(args[1..]);

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
              run        常驻录制，带托盘图标（可选 --show 立即打开主窗口）
              ui         打开独立桌面监控窗口（已有常驻端时优先唤醒，否则以只读视图打开）
              collect    前台录制，调试用，Ctrl+C 结束
              stats      把 events.jsonl 归因成 stats.json 与 typos.jsonl
              label      导出金标标注表，人工打勾后用来校准判定规则
              export     把统计翻译成别人的格式（支持 keybr / csv / json）
              words      按弱键生成中文或符号练习串
              dashboard  生成单文件 HTML 看板，双击就开
              drill      生成单文件练习页，一屏一个字，练完给成绩
              pause      暂停录制（等同托盘菜单 / Ctrl+Alt+P）
              resume     继续录制
              stop       让常驻进程退出

            选项：
              --out <目录>   数据目录，默认 %USERPROFILE%\.keytrace\events
              -h, --help     显示本帮助
              --version      显示版本

            ui 专用：
              --viewer       强制以独立只读视图启动窗口（不抢占常驻托盘锁）

            stats 专用：
              --in <目录>        事件目录，默认同上
              --out <文件>       统计文件，默认 %USERPROFILE%\.keytrace\stats.json
              --typos <文件>     逐条明细，默认与统计文件同目录的 typos.jsonl
              --since <范围>     7d / 全部写 all / 起始日期 2026-11-01，默认 7d
              --pause-factor <n> 停顿门槛 = IKI 中位数 × n，默认 3
              --min-samples <n>  单键样本门槛，默认 20
              --acc-threshold <n> 准确率护栏阈值（%），低于此值提醒，默认 95

            label 专用：
              --out <文件>       标注表，默认 %USERPROFILE%\.keytrace\labeling-日期.csv
              --total <n>        共导出多少条，默认 200
              --seed <n>         抽样种子，默认 20261117（同种子导两次结果一样）
              --anonymize        去掉文字、应用、时间，只留结构特征（准备公开时用）

            export 专用：
              --format <名字>    目标格式：keybr / csv / json（默认 keybr）
              --out <文件>       默认 typing-data.json / keys.csv / stats.json
              --in / --since / --pause-factor / --min-samples  同 stats

            words 专用：
              --lang <语言>      zh（中文双拼，默认）或 sym（编程符号）
              --weak <键或符号>  弱键（如 c,v,f 或 {};[]=）；不给就从 stats.json 取
              --count <n>        生成多少个字/串，默认 40
              --out <文件>       默认 weak-zh.txt / weak-sym.txt

            dashboard 专用：
              --stats <文件>     读哪个统计，默认 %USERPROFILE%\.keytrace\stats.json
              --out <文件>       默认 %USERPROFILE%\.keytrace\report.html

            drill 专用：
              --weak <字母>      弱键，逗号分隔；不给就结合练习记录与 stats.json 推荐
              --count <n>        练多少个字，默认 60
              --out <文件>       默认 %USERPROFILE%\.keytrace\drill.html
              --drills <目录>    练习回流目录，默认 %USERPROFILE%\.keytrace\drills
              --record <来源>    录入练完的 JSON（文件路径 / JSON 串 / - 从标准输入读）

            全部数据只存本地，不联网。
            """);
    }

    /// <summary>常驻模式：单实例 + 托盘 + 热键 + 采集 + 桌面窗口，靠 WPF Application 消息循环驱动。</summary>
    private static int RunDaemon(string[] args)
    {
        string target = ResolveDataDirectory(args);
        string baseDir = UserConfig.BaseDirectoryFromEvents(target);
        UserConfig config = UserConfig.Load(baseDir);

        using Mutex? instance = ControlSignals.TryAcquireInstance();
        if (instance is null)
        {
            Console.Error.WriteLine("已经有一个 KeyTrace 在运行了。");
            return 1;
        }

        (EventWaitHandle stopSignal, EventWaitHandle pauseSignal, EventWaitHandle showSignal) = ControlSignals.CreateServerSide();
        using EventWaitHandle stop = stopSignal;
        using EventWaitHandle pause = pauseSignal;
        using EventWaitHandle show = showSignal;

        bool captureText = HasFlag(args, "--no-text") ? false : config.CaptureText;
        ExclusionList exclusions = new(config.ExtraExclusions);
        using EventWriter writer = new(target);
        using RawInputCollector collector = new(
            writer,
            stop,
            pause,
            exclusions: exclusions,
            captureText: captureText,
            traceText: HasFlag(args, "--debug-text"),
            showSignal: show,
            imeOverride: config.ImeOverride);

        return DesktopAppHost.RunDaemon(
            collector,
            writer,
            config,
            baseDir,
            showOnStart: HasFlag(args, "--show"));
    }

    /// <summary>打开独立桌面窗口：优先唤醒已有常驻端；已有不可唤醒的后台端时回落为实时只读视图。</summary>
    private static int RunUi(string[] args)
    {
        string target = Value(args, "--in") ?? ResolveDataDirectory(args);
        string baseDir = UserConfig.BaseDirectoryFromEvents(target);
        UserConfig config = UserConfig.Load(baseDir);

        bool forceViewer = HasFlag(args, "--viewer");
        if (!forceViewer && ControlSignals.SignalShow())
        {
            return 0;
        }

        if (!forceViewer)
        {
            using Mutex? probe = ControlSignals.TryAcquireInstance();
            if (probe is not null)
            {
                probe.ReleaseMutex();
                probe.Dispose();
                return RunDaemon(["--out", target, "--show"]);
            }
        }

        return DesktopAppHost.RunViewer(target, baseDir, config);
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

        if (!TryParseAttributionArgs(args, out double pauseFactor, out int minSamples))
        {
            return 1;
        }

        double accThreshold = AccuracyGuard.DefaultThresholdPercent;
        if (Value(args, "--acc-threshold") is string accText
            && (!double.TryParse(accText, NumberStyles.Float, CultureInfo.InvariantCulture, out accThreshold)
                || accThreshold <= 0
                || accThreshold > 100))
        {
            Console.Error.WriteLine("--acc-threshold 需要 0 到 100 之间的数字，例如 95。");
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

        StatsContext context = MakeStatsContext(directory, files, events.Count, since, today, options);

        StatsBuilder.WriteJson(output, result, context);
        StatsBuilder.WriteTypos(typos, result);

        AccuracyReport guard = AccuracyGuard.Evaluate(result, accThreshold);

        Console.WriteLine($"事件 {events.Count} 条（{files.Count} 个文件，跳过 {result.Skipped.Total + rejected} 条）");
        Console.WriteLine($"击键 {result.Totals.Keystrokes}，上屏 {result.Totals.Chars} 字，会话 {result.Totals.Sessions}");
        Console.WriteLine($"打错 {result.Totals.Typos}（拼音层 {result.Totals.PinyinErrors}），选字错 {result.Totals.SelectionErrors}，改措辞 {result.Totals.Rewording}");
        Console.WriteLine($"中文 {result.Chinese.Commits} 条上屏，带组字中间态 {CompCoverage(result)}");
        Console.WriteLine($"准确率 {guard.AccuracyPercent:0.0}%（护栏阈值 {guard.ThresholdPercent:0.#}%）");
        if (guard.Triggered)
        {
            Console.WriteLine($"! 准确率护栏触发：当前 {guard.AccuracyPercent:0.0}% 低于 {guard.ThresholdPercent:0.#}%，建议先放慢节奏保准。");
            if (guard.TopErrorKeys.Count > 0)
            {
                Console.WriteLine("  错误最多的键：" + string.Join("  ",
                    guard.TopErrorKeys.Select(k => $"{k.Name}({k.Typos}/{k.Presses}, {k.TypoRate * 100:0.0}%)")));
            }
        }

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

    /// <summary>导出成外部工具或表格能直接使用的格式（keybr / csv / json）。</summary>
    private static int ExportData(string[] args)
    {
        ConsoleBridge.Attach();

        string format = (Value(args, "--format") ?? "keybr").ToLowerInvariant();
        if (format is not ("keybr" or "csv" or "json"))
        {
            Console.Error.WriteLine($"不认识的格式：{format}。目前支持 keybr / csv / json。");
            return 1;
        }

        string directory = Value(args, "--in") ?? DefaultDataDirectory();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        if (!TryParseSince(Value(args, "--since") ?? "7d", today, out DateOnly? since))
        {
            Console.Error.WriteLine("--since 只接受 7d、all 或 2026-11-01 这样的日期。");
            return 1;
        }

        if (!TryParseAttributionArgs(args, out double pauseFactor, out int minSamples))
        {
            return 1;
        }

        string defaultFile = format switch
        {
            "csv" => "keys.csv",
            "json" => "stats.json",
            _ => "typing-data.json",
        };
        string output = Value(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            defaultFile);
        if (Directory.Exists(output))
        {
            output = Path.Combine(output, defaultFile);
        }

        (List<string> files, IEnumerable<EventRecord> stream, int rejected) = EventLog.Read(directory, since, today);
        List<EventRecord> events = [.. stream];
        if (events.Count == 0)
        {
            Console.Error.WriteLine($"没有可读的事件：{directory}");
            return 1;
        }

        AttributorOptions options = new()
        {
            PauseFactor = pauseFactor,
            MinKeySamples = minSamples,
        };

        if (format == "keybr")
        {
            // 一天一个 session：keybr 的一次练习就是这么定义的，把两个月合成一个 session
            // 会让"这一课练完的进步"完全看不出来。
            List<(DateOnly Day, AttributionResult Result)> days = [];
            foreach (IGrouping<DateOnly, EventRecord> group in events.GroupBy(DayOf))
            {
                days.Add((group.Key, new Attributor(options).Run([.. group], rejected)));
            }

            days.Sort((a, b) => a.Day.CompareTo(b.Day));

            string json = KeybrExport.Build(days);
            KeybrExport.Write(output, json);

            int sessions = days.Count(d => KeybrExport.KeysOf(d.Result).Count > 0);
            int keys = days.SelectMany(d => KeybrExport.KeysOf(d.Result)).Select(k => k.CodePoint).Distinct().Count();

            Console.WriteLine($"事件 {events.Count} 条（{files.Count} 个文件），{days.Count} 天");
            Console.WriteLine($"导出 {sessions} 个 session，覆盖 {keys} 个字母/数字键");
            Console.WriteLine();
            Console.WriteLine($"→ {output}");
            Console.WriteLine();
            Console.WriteLine("导入 keybr-tui：");
            Console.WriteLine($"  keybr-tui --import \"{output}\"");
            Console.WriteLine("（会先备份本地统计；加 --force 才覆盖）");
            return 0;
        }

        AttributionResult result = new Attributor(options).Run(events, rejected);
        if (format == "csv")
        {
            string csv = CsvExport.Build(result, minSamples);
            CsvExport.Write(output, csv);
            Console.WriteLine($"事件 {events.Count} 条（{files.Count} 个文件），导出 {result.Keys.Count} 个键");
            Console.WriteLine($"→ {output}");
            return 0;
        }

        StatsContext context = MakeStatsContext(directory, files, events.Count, since, today, options);
        StatsBuilder.WriteJson(output, result, context);
        Console.WriteLine($"事件 {events.Count} 条（{files.Count} 个文件），跳过 {result.Skipped.Total + rejected} 条");
        Console.WriteLine($"→ {output}");
        return 0;
    }

    private static StatsContext MakeStatsContext(
        string directory,
        List<string> files,
        int eventCount,
        DateOnly? since,
        DateOnly today,
        AttributorOptions options) => new()
    {
        Directory = directory,
        Files = files,
        Events = eventCount,
        From = since ?? EarliestFileDate(files) ?? today,
        To = today,
        PauseFactor = options.PauseFactor,
        TypoMaxBackspaces = options.TypoMaxBackspaces,
        MinKeySamples = options.MinKeySamples,
    };

    private static bool TryParseAttributionArgs(string[] args, out double pauseFactor, out int minSamples)
    {
        pauseFactor = 3.0;
        minSamples = 20;

        if (Value(args, "--pause-factor") is string factorText
            && (!double.TryParse(factorText, NumberStyles.Float, CultureInfo.InvariantCulture, out pauseFactor) || pauseFactor <= 0))
        {
            Console.Error.WriteLine("--pause-factor 需要一个正小数，例如 3。");
            return false;
        }

        if (Value(args, "--min-samples") is string sampleText
            && (!int.TryParse(sampleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out minSamples) || minSamples < 1))
        {
            Console.Error.WriteLine("--min-samples 需要正整数，例如 20。");
            return false;
        }

        return true;
    }

    /// <summary>生成练习页或录入练完的成绩：一屏一个字，打小鹤双拼的两键。</summary>
    private static int BuildDrill(string[] args)
    {
        ConsoleBridge.Attach();

        string drillsDir = Value(args, "--drills") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "drills");

        if (Value(args, "--record") is string recordArg)
        {
            if (!TryRecordDrill(drillsDir, recordArg))
            {
                return 1;
            }

            if (Value(args, "--out") is null)
            {
                return 0;
            }
        }

        int count = 60;
        if (Value(args, "--count") is string countText && (!int.TryParse(countText, out count) || count < 1))
        {
            Console.Error.WriteLine("--count 需要正整数。");
            return 1;
        }

        List<char> weak;
        string source;
        if (Value(args, "--weak") is string spec)
        {
            weak = [.. spec.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').Distinct()];
            source = "命令行指定";
        }
        else
        {
            weak = ResolveDrillWeakKeys(drillsDir, out source);
        }

        if (weak.Count == 0)
        {
            Console.Error.WriteLine("没有弱键可用：先跑 keytrace stats，或先用 --record 录入练习结果，或用 --weak c,v 手动指定。");
            return 1;
        }

        List<HanziEntry> picked = ZhSelector.Pick(new HashSet<char>(weak), count);
        if (picked.Count == 0)
        {
            Console.Error.WriteLine("字表是空的——data/zh-hanzi.tsv 可能没被打进程序集。");
            return 1;
        }

        string items = BuildDrillItemsJson(picked);
        string title = $"{picked.Count} 个字 · 小鹤双拼练习";

        string output = Value(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "drill.html");

        DrillPage.Write(output, items, title);

        Console.WriteLine($"弱键 {string.Join(' ', weak.Select(char.ToUpperInvariant))}（{source}）");
        Console.WriteLine($"{picked.Count} 个字，覆盖 " + string.Join(" ",
            weak.Select(w => $"{char.ToUpperInvariant(w)}×{picked.Count(p => p.Code.Contains(w))}")));
        Console.WriteLine();
        Console.WriteLine($"→ {output}");
        Console.WriteLine("双击打开，点一下页面，然后按键盘上对应的两个字母。");
        Console.WriteLine("练完把页面底部的 JSON 存下来，跑 keytrace drill --record <文件> 就能按表现出题。");

        return 0;
    }

    private static bool TryRecordDrill(string drillsDir, string recordArg)
    {
        string rawJson;
        if (recordArg == "-")
        {
            rawJson = Console.In.ReadToEnd();
        }
        else if (recordArg.TrimStart().StartsWith('{'))
        {
            rawJson = recordArg;
        }
        else if (File.Exists(recordArg))
        {
            rawJson = File.ReadAllText(recordArg, System.Text.Encoding.UTF8);
        }
        else
        {
            Console.Error.WriteLine($"找不到练习结果文件：{recordArg}");
            return false;
        }

        try
        {
            (string savedPath, DrillRecord record) = DrillHistory.Save(drillsDir, rawJson);
            List<DrillRecord> recent = DrillHistory.LoadRecent(drillsDir);
            List<char> statsWeak = WeakKeysFromStats(out _);
            List<char> recommended = DrillHistory.RecommendWeakKeys(recent, statsWeak);

            Console.WriteLine($"已保存练习结果 → {savedPath}");
            Console.WriteLine($"本次：完成 {record.Correct}/{record.Total}，准确率 {record.Accuracy:0.0}%，用时 {record.ElapsedMs / 1000.0:0.0} 秒");
            if (recommended.Count > 0)
            {
                Console.WriteLine($"更新后推荐弱键：{string.Join(' ', recommended.Select(char.ToUpperInvariant))}");
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Console.Error.WriteLine($"录入失败：{ex.Message}");
            return false;
        }
    }

    private static List<char> ResolveDrillWeakKeys(string drillsDir, out string source)
    {
        List<char> statsWeak = WeakKeysFromStats(out string statsNote);
        List<DrillRecord> recent = DrillHistory.LoadRecent(drillsDir);
        List<char> recommended = DrillHistory.RecommendWeakKeys(recent, statsWeak);

        if (recent.Count > 0 && statsWeak.Count > 0)
        {
            source = $"近 {recent.Count} 次练习回流 + {statsNote}";
        }
        else if (recent.Count > 0)
        {
            source = $"来自近 {recent.Count} 次练习回流";
        }
        else
        {
            source = statsNote;
        }

        return recommended;
    }

    internal static string BuildDrillItemsJson(List<HanziEntry> picked)
    {
        using MemoryStream stream = new();
        using (System.Text.Json.Utf8JsonWriter w = new(stream, new System.Text.Json.JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartArray();
            foreach (HanziEntry e in picked)
            {
                w.WriteStartObject();
                w.WriteString("c", e.Char.ToString());
                w.WriteString("p", e.Syllable);
                w.WriteString("k", e.Code);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>生成自包含 HTML 看板。数据内嵌，不联网、不发请求。</summary>
    private static int BuildDashboard(string[] args)
    {
        ConsoleBridge.Attach();

        string statsPath = Value(args, "--stats") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "stats.json");

        if (!File.Exists(statsPath))
        {
            Console.Error.WriteLine($"找不到 {statsPath}，先跑 keytrace stats。");
            return 1;
        }

        string output = Value(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "report.html");

        string json = File.ReadAllText(statsPath);
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"{statsPath} 不是合法 JSON：{ex.Message}");
            return 1;
        }

        Dashboard.Write(output, json);

        long size = new FileInfo(output).Length;
        Console.WriteLine($"→ {output}（{size / 1024} KB，数据已内嵌，断网也能开）");
        return 0;
    }

    /// <summary>按弱键生成练习串：中文按双拼码反查字，符号用真实代码片段。</summary>
    private static int BuildWords(string[] args)
    {
        ConsoleBridge.Attach();

        string lang = (Value(args, "--lang") ?? "zh").ToLowerInvariant();
        if (lang is not ("zh" or "sym"))
        {
            Console.Error.WriteLine($"不支持的语言：{lang}。目前支持 zh（中文双拼）/ sym（编程符号）；英文请用 keytrace export --format keybr。");
            return 1;
        }

        int count = 40;
        if (Value(args, "--count") is string countText && (!int.TryParse(countText, out count) || count < 1))
        {
            Console.Error.WriteLine("--count 需要正整数。");
            return 1;
        }

        if (lang == "sym")
        {
            return BuildSymbolWords(args, count);
        }

        List<char> weak;
        string source;
        if (Value(args, "--weak") is string spec)
        {
            weak = [.. spec.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').Distinct()];
            source = "命令行指定";
        }
        else
        {
            weak = WeakKeysFromStats(out string note);
            source = note;
        }

        if (weak.Count == 0)
        {
            Console.Error.WriteLine("没有弱键可用：先跑 keytrace stats 攒够数据，或者用 --weak c,v 手动指定。");
            return 1;
        }

        HashSet<char> weakSet = [.. weak];
        List<HanziEntry> picked = ZhSelector.Pick(weakSet, count);
        if (picked.Count == 0)
        {
            Console.Error.WriteLine("字表是空的——data/zh-hanzi.tsv 可能没被打进程序集。");
            return 1;
        }

        string output = Value(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "weak-zh.txt");

        // 一行 10 个字：粘进 keybr 的自定义文本或 monkeytype 就是一段练习
        List<string> lines = [];
        for (int i = 0; i < picked.Count; i += 10)
        {
            lines.Add(string.Join(' ', picked.Skip(i).Take(10).Select(p => p.Char)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllLines(output, lines, new System.Text.UTF8Encoding(false));

        Dictionary<char, int> coverage = ZhSelector.Coverage(picked);
        Console.WriteLine($"弱键 {string.Join(' ', weak.Select(char.ToUpperInvariant))}（{source}）→ {picked.Count} 字");
        Console.WriteLine();
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("覆盖：" + string.Join("  ",
            weak.Select(w => $"{char.ToUpperInvariant(w)}×{coverage.GetValueOrDefault(w)}")));
        Console.WriteLine($"混入 {picked.Count - picked.Count(p => p.Code.Any(weakSet.Contains))} 个已掌握键的字（约 {ZhSelector.KnownMix:P0}），避免偏科");
        Console.WriteLine();
        Console.WriteLine($"→ {output}");
        Console.WriteLine();
        Console.WriteLine("对照表（字：音节 = 双拼两键）：");
        foreach (HanziEntry e in picked.Take(6))
        {
            Console.WriteLine($"  {e.Char}：{e.Syllable} = {e.Code[0]} + {e.Code[1]}");
        }

        return 0;
    }

    private static int BuildSymbolWords(string[] args, int count)
    {
        HashSet<char> weakSet;
        string source;
        if (Value(args, "--weak") is string spec)
        {
            weakSet = SymbolSelector.ParseWeakSpec(spec);
            source = "命令行指定";
        }
        else
        {
            string statsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".keytrace",
                "stats.json");
            if (File.Exists(statsPath))
            {
                weakSet = [.. SymbolSelector.WeakSymbolsFromStatsJson(File.ReadAllText(statsPath))];
                source = "来自 stats.json（无符号错时回落默认常用符号）";
            }
            else
            {
                weakSet = [.. SymbolSelector.DefaultWeakSymbols];
                source = "默认常用代码符号";
            }
        }

        if (weakSet.Count == 0)
        {
            Console.Error.WriteLine("没有弱符号可用，请用 --weak \"{}\" 手动指定。");
            return 1;
        }

        List<string> picked = SymbolSelector.Pick(weakSet, count);
        if (picked.Count == 0)
        {
            Console.Error.WriteLine($"内置符号词库里没有包含 {string.Join(' ', weakSet)} 的代码串。");
            return 1;
        }

        string output = Value(args, "--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "weak-sym.txt");

        List<string> lines = [];
        for (int i = 0; i < picked.Count; i += 8)
        {
            lines.Add(string.Join(' ', picked.Skip(i).Take(8)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllLines(output, lines, new System.Text.UTF8Encoding(false));

        Dictionary<char, int> coverage = SymbolSelector.Coverage(picked);
        Console.WriteLine($"弱符号 {string.Join(' ', weakSet)}（{source}）→ {picked.Count} 串");
        Console.WriteLine();
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("覆盖：" + string.Join("  ",
            weakSet.Select(w => $"{w}×{coverage.GetValueOrDefault(w)}")));
        Console.WriteLine($"混入 {picked.Count - picked.Count(p => p.Any(weakSet.Contains))} 个已掌握符号串（约 {SymbolSelector.KnownMix:P0}），避免偏科");
        Console.WriteLine();
        Console.WriteLine($"→ {output}");
        return 0;
    }

    /// <summary>从 stats.json 取最弱的五个字母键。符号键不进——中文练习只认 a–z。</summary>
    private static List<char> WeakKeysFromStats(out string note)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "stats.json");

        note = "手动指定";
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using FileStream file = File.OpenRead(path);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(file);

            List<(string Name, double Score)> ranked = [];
            foreach (System.Text.Json.JsonElement key in doc.RootElement.GetProperty("keys").EnumerateArray())
            {
                System.Text.Json.JsonElement score = key.GetProperty("weakScore");
                if (score.ValueKind != System.Text.Json.JsonValueKind.Number)
                {
                    continue;
                }

                string name = key.GetProperty("name").GetString() ?? "";
                if (name.Length == 1 && name[0] is >= 'A' and <= 'Z')
                {
                    ranked.Add((name, score.GetDouble()));
                }
            }

            ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
            List<char> weak = [.. ranked.Take(5).Select(r => char.ToLowerInvariant(r.Name[0]))];
            note = $"来自 stats.json 前 {weak.Count} 名";
            return weak;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static DateOnly DayOf(EventRecord e) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(e.Ts).ToLocalTime().Date);

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
