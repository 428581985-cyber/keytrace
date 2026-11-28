using System;
using System.IO;
using System.Linq;
using KeyTrace.Analysis;
using KeyTrace.Collect;
using KeyTrace.Control;
using KeyTrace.Ui;

namespace KeyTrace.Tests;

/// <summary>
/// 不依赖任何测试框架：离线环境装不了 NuGet 包，一个小断言器足够。
/// 失败时退出码非 0，CI 直接就能用。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        DiffTests();
        TextClassificationTests();
        SessionTests();
        RotatorTests();
        AttributionTests();
        WeakScoreTests();
        LabelExportTests();
        KeybrExportTests();
        CsvExportTests();
        ShuangpinTests();
        ZhSelectionTests();
        SymbolSelectionTests();
        AccuracyGuardTests();
        DashboardTests();
        DrillPageTests();
        DrillHistoryTests();
        UserConfigTests();
        StandaloneDashboardAndTrayTests();
        ExclusionListTests();
        LiveTelemetryTests();
        DesktopStatsServiceTests();
        ControlSignalsTests();
        AppIconsTests();
        ImeConfigTests();
        CollectorFilterTests();

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void DiffTests()
    {
        Section("Diff");
        Eq("追加一段", "你好", UiTextSampler.Diff(string.Empty, "你好"));
        Eq("在已有内容后追加", "世界", UiTextSampler.Diff("你好", "你好世界"));
        Eq("纯删除不算输入", string.Empty, UiTextSampler.Diff("你好世界", "你好"));
        Eq("开头插入", "前", UiTextSampler.Diff("后", "前后"));
        Eq("中间替换", "B", UiTextSampler.Diff("A后", "AB后"));
        Eq("整篇被替换时不当作输入", string.Empty, UiTextSampler.Diff("短", new string('x', 300)));
        Eq("无变化", string.Empty, UiTextSampler.Diff("相同", "相同"));
    }

    private static void TextClassificationTests()
    {
        Section("文本分类");
        True("含汉字", UiTextSampler.ContainsNonAscii("你好"));
        False("纯 ASCII", UiTextSampler.ContainsNonAscii("nihc"));
        True("全拼带分隔符也算 ASCII", !UiTextSampler.ContainsNonAscii("ni'hao"));
        True("file URL", UiTextSampler.LooksLikeUrl("file:///C:/a.html"));
        True("https URL", UiTextSampler.LooksLikeUrl("https://example.com"));
        False("普通文本不是 URL", UiTextSampler.LooksLikeUrl("你好"));

        Eq("去掉图标字体字符", "abc", UiTextSampler.StripPrivateUse("\uE000abc\uF8FF"));
        Eq("纯图标字符变成空", string.Empty, UiTextSampler.StripPrivateUse("\uea88\ueb62"));
        Eq("没有图标字符就原样返回", "你好", UiTextSampler.StripPrivateUse("你好"));
    }

    private static void CollectorFilterTests()
    {
        Section("采集过滤");
        True("RuntimeId 相同", UiTextSampler.RuntimeIdEquals([42, 7], [42, 7]));
        False("RuntimeId 不同判定为切换控件", UiTextSampler.RuntimeIdEquals([42, 7], [42, 8]));
        False("RuntimeId 一方为空不同", UiTextSampler.RuntimeIdEquals([42, 7], null));
        True("RuntimeId 同为空", UiTextSampler.RuntimeIdEquals(null, null));

        True("普通字母计入击键", RawInputCollector.ShouldCountTypingKey(65, 0, isUp: false, isRepeat: false));
        True("Shift 大写字母计入击键", RawInputCollector.ShouldCountTypingKey(65, 1, isUp: false, isRepeat: false));
        False("Ctrl+C 快捷键不计入打字键数", RawInputCollector.ShouldCountTypingKey(67, 2, isUp: false, isRepeat: false));
        False("Alt 快捷键不计入打字键数", RawInputCollector.ShouldCountTypingKey(65, 4, isUp: false, isRepeat: false));
        False("Shift 修饰键本身不计入打字键数", RawInputCollector.ShouldCountTypingKey(160, 1, isUp: false, isRepeat: false));
        False("Ctrl 修饰键本身不计入打字键数", RawInputCollector.ShouldCountTypingKey(162, 2, isUp: false, isRepeat: false));
        False("非退格长按连发不重复计数", RawInputCollector.ShouldCountTypingKey(65, 0, isUp: false, isRepeat: true));
        True("退格长按连发允许计数", RawInputCollector.ShouldCountTypingKey(8, 0, isUp: false, isRepeat: true));
        False("抬起事件不计数", RawInputCollector.ShouldCountTypingKey(65, 0, isUp: true, isRepeat: false));
    }

    private static void SessionTests()
    {
        Section("会话切分");
        DateOnly day = new(2026, 11, 16);

        SessionTracker tracker = new();
        string first = tracker.Observe("a.exe", 1000, day);
        string second = tracker.Observe("a.exe", 2000, day);
        Eq("同应用短间隔不切", first, second);

        string third = tracker.Observe("a.exe", 2000 + 6000, day);
        True("空闲超过 5 秒要切", third != second);

        string fourth = tracker.Observe("b.exe", 2000 + 6100, day);
        True("换应用要切", fourth != third);

        string fifth = tracker.Observe("b.exe", 2000 + 6200, day.AddDays(1));
        True("跨天要切", fifth != fourth);
    }

    private static void RotatorTests()
    {
        Section("日志轮转");
        string dir = Path.Combine(Path.GetTempPath(), "keytrace-rotate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            DateOnly today = new(2026, 11, 16);
            string old = Path.Combine(dir, "2026-11-10.jsonl");
            string fresh = Path.Combine(dir, "2026-11-16.jsonl");
            File.WriteAllText(old, "{\"v\":1}\n");
            File.WriteAllText(fresh, "{\"v\":1}\n");

            LogRotator.Run(dir, today);

            False("过期文件被压走", File.Exists(old));
            True("压缩包存在", File.Exists(old + ".gz"));
            True("当天文件不动", File.Exists(fresh));

            string expired = Path.Combine(dir, "2026-09-01.jsonl.gz");
            File.WriteAllText(expired, "x");
            LogRotator.Run(dir, today);
            False("超过 30 天的压缩包被删除", File.Exists(expired));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void AttributionTests()
    {
        Section("归因");

        // 前面 20 个键只为了把 IKI 中位数钉在 200ms，门槛 = 200 × 3 = 600ms
        AttributionResult replaced = Analyze(
            Kd(3000, 84), // t
            Kd(3200, 72), // h
            Kd(3400, 82), // r —— 本来要按 e
            Kd(3600, 8),  // 退格
            Kd(3800, 69)); // e
        Eq("一次退格算一个 typo", "1", replaced.Totals.Typos.ToString());
        Eq("相邻键替换判为高置信度", "adjacentSubstitution", replaced.Records[0].Cause);
        Eq("被删的键是 R", "R", replaced.Records[0].Key);
        Eq("替换字符记为 e", "e", replaced.Records[0].Replacement?.ToString() ?? "null");
        Eq("不误记为拼音层", "0", replaced.Totals.PinyinErrors.ToString());

        AttributionResult paused = Analyze(
            Kd(3000, 84),
            Kd(5000, 8)); // 停了 2 秒才退格
        Eq("长停顿后的退格判为改措辞", "1", paused.Totals.RewordingLongPause.ToString());
        Eq("长停顿不计 typo", "0", paused.Totals.Typos.ToString());

        AttributionResult chained = Analyze(
            Kd(3000, 65), Kd(3200, 66), Kd(3400, 67), Kd(3600, 68),
            Kd(3800, 8), Kd(4000, 8), Kd(4200, 8), Kd(4400, 8));
        Eq("连续 4 次退格超出上限，判为改措辞", "4", chained.Totals.RewordingLongRun.ToString());
        Eq("连锁退格不计 typo", "0", chained.Totals.Typos.ToString());

        AttributionResult pinyin = Analyze(
            Kd(3000, 78), // n
            Kd(3200, 73), // i
            Kd(3400, 72), // h
            Kd(3600, 86), // v —— 韵母键按错
            Kd(3800, 8),
            Kd(4000, 67), // c
            Comp(4200, "nihc"),
            Commit(4600, "你好", 5));
        Eq("拼音层错计数", "1", pinyin.Totals.PinyinErrors.ToString());
        Eq("拼音层错算进打字统计", "1", pinyin.Totals.Typos.ToString());
        Eq("错在 V 键", "1", pinyin.Keys[86].PinyinTypos.ToString());
        Eq("带组字中间态的中文上屏", "1", pinyin.Chinese.CommitsWithComp.ToString());

        AttributionResult selection = Analyze(
            Commit(3000, "你好", 4),
            Kd(3400, 8));
        Eq("汉字上屏后立刻退格算选字错", "1", selection.Totals.SelectionErrors.ToString());
        Eq("选字错不进打字统计", "0", selection.Totals.Typos.ToString());

        AttributionResult noComp = Analyze(
            Kd(3000, 78), Kd(3200, 73), Kd(3400, 72),
            Kd(3600, 8),
            Commit(4000, "你", 3));
        Eq("没有 comp 的中文退格不猜成拼音层", "0", noComp.Totals.PinyinErrors.ToString());
        Eq("没有 comp 的退格算普通 typo", "1", (noComp.Totals.Typos + noComp.Totals.TyposUnreplaced).ToString());
        Eq("compCoverage 只统计到 1 条", "0", noComp.Chinese.CommitsWithComp.ToString());

        AttributionResult unreplaced = Analyze(
            Kd(3000, 84),
            Kd(3200, 72),
            Kd(3400, 8)); // 打了 th，删掉 h，后面什么都没补
        Eq("删完不补的不算确认纠错", "0", unreplaced.Totals.Typos.ToString());
        Eq("删完不补的单独计数", "1", unreplaced.Totals.TyposUnreplaced.ToString());
        Eq("该键的确认错误为 0", "0", unreplaced.Keys[72].Typos.ToString());

        AttributionResult pasted = Analyze(false, Commit(3000, "粘贴进来的一段", 0));
        Eq("keys 为 0 的 commit 是粘贴", "1", pasted.Skipped.Paste.ToString());
        Eq("粘贴不产生字符统计", "0", pasted.Totals.Chars.ToString());

        AttributionResult bulk = Analyze(false, Commit(3000, new string('a', 200), 2));
        Eq("字符数超过击键数的上屏是自动补全/整篇插入", "1", bulk.Skipped.BulkCommits.ToString());
        Eq("批量上屏不计入字数", "0", bulk.Totals.Chars.ToString());

        AttributionResult blind = Analyze(
            false,
            Kd(3000, 84), Kd(3200, 72), Kd(3400, 8), Kd(3600, 69));
        Eq("读不到文字的会话里退格不判打错", "0", blind.Totals.Typos.ToString());
        Eq("这类退格单独计数", "1", blind.Totals.BlindDeletions.ToString());

        AttributionResult glyph = Analyze(Commit(3000, "\uea88", 5));
        Eq("私有区字形被丢弃", "1", glyph.Skipped.Glyph.ToString());

        AttributionResult synthetic = Analyze(
            new EventRecord(1, 3000, 3000, "kd", 65, 0, 0, "a.exe", "s1", null, null, null));
        Eq("扫描码为 0 判为合成数据", "1", synthetic.Skipped.Synthetic.ToString());

        AttributionResult shortcut = Analyze(
            new EventRecord(1, 1_700_000_003_000, 3000, "kd", 80, 25, 2, "a.exe", "s1", null, null, null));
        Eq("带 Ctrl 的按键不算打字", "1", shortcut.Skipped.Shortcut.ToString());

        // 多轮重启后 mono 会从 0 重计，不能按 mono 把不同会话交错到一起
        System.Collections.Generic.List<EventRecord> restarted = [];
        for (int i = 0; i < 20; i++)
        {
            long m = 100 + i * 200;
            restarted.Add(new EventRecord(1, 1_700_000_000_000 + m, m, "kd", 65 + (i % 26), 30, 0, "base.exe", "base", null, null, null));
        }

        long ts1 = 1_700_000_010_000;
        restarted.AddRange([
            new EventRecord(1, ts1 + 100, 100, "kd", 84, 20, 0, "a.exe", "s-run1", null, null, null),
            new EventRecord(1, ts1 + 300, 300, "kd", 82, 19, 0, "a.exe", "s-run1", null, null, null),
            new EventRecord(1, ts1 + 500, 500, "kd", 8, 14, 0, "a.exe", "s-run1", null, null, null),
            new EventRecord(1, ts1 + 700, 700, "kd", 69, 18, 0, "a.exe", "s-run1", null, null, null),
            new EventRecord(1, ts1 + 900, 900, "commit", 0, 0, 0, "a.exe", "s-run1", "te", "uia", 4),
        ]);

        long ts2 = 1_700_000_020_000;
        restarted.AddRange([
            new EventRecord(1, ts2 + 100, 100, "kd", 65, 30, 0, "a.exe", "s-run2", null, null, null),
            new EventRecord(1, ts2 + 300, 300, "kd", 87, 17, 0, "a.exe", "s-run2", null, null, null),
            new EventRecord(1, ts2 + 500, 500, "kd", 8, 14, 0, "a.exe", "s-run2", null, null, null),
            new EventRecord(1, ts2 + 700, 700, "kd", 81, 16, 0, "a.exe", "s-run2", null, null, null),
            new EventRecord(1, ts2 + 900, 900, "commit", 0, 0, 0, "a.exe", "s-run2", "aq", "uia", 4),
        ]);

        AttributionResult multiRun = new Attributor().Run(restarted);
        Eq("跨重启同 mono 不交错，两个会话都判出打错", "2", multiRun.Totals.Typos.ToString());

        // 同会话内 ts 倒退（如 NTP 对时）仍按 mono 排（故意打乱输入顺序验排序本身）
        AttributionResult clockSkew = Analyze(
            new EventRecord(1, 1_700_000_005_500, 500, "kd", 8, 14, 0, "a.exe", "s1", null, null, null),
            new EventRecord(1, 1_700_000_010_100, 100, "kd", 84, 20, 0, "a.exe", "s1", null, null, null),
            new EventRecord(1, 1_700_000_005_700, 700, "kd", 69, 18, 0, "a.exe", "s1", null, null, null),
            new EventRecord(1, 1_700_000_005_300, 300, "kd", 82, 19, 0, "a.exe", "s1", null, null, null));
        Eq("同会话 ts 倒退仍按 mono 归因", "1", clockSkew.Totals.Typos.ToString());
        Eq("同会话 ts 倒退时被删字符是 r", "r", clockSkew.Records[0].Deleted?.ToString() ?? "null");

        // 符号键打错应计入 symbols[].typos
        AttributionResult symTypo = Analyze(
            new EventRecord(1, 1_700_000_003_000, 3000, "kd", 219, 26, 1, "a.exe", "s1", null, null, null), // {
            Kd(3200, 8),
            new EventRecord(1, 1_700_000_003_400, 3400, "kd", 219, 26, 0, "a.exe", "s1", null, null, null), // [
            Commit(3600, "[", 3));
        Eq("符号打错计入 typos", "1", symTypo.Totals.Typos.ToString());
        Eq("符号明细同步记录 typos", "1", (symTypo.Symbols.TryGetValue('{', out SymbolStat? brace) ? brace.Typos : 0).ToString());
    }

    private static void WeakScoreTests()
    {
        Section("弱键评分");
        KeyStat few = new() { Vk = 90, Presses = 5, Typos = 2 };
        Eq("样本不足不排名", "null", StatsBuilder.WeakScore(few, 1.0, 20)?.ToString("0.###") ?? "null");

        KeyStat bad = new() { Vk = 90, Presses = 100, Typos = 15 };
        Eq("错误率拉满时只看速度项", "0.7", StatsBuilder.WeakScore(bad, 1.0, 20)!.Value.ToString("0.###"));

        KeyStat slow = new() { Vk = 90, Presses = 100, Typos = 0 };
        Eq("零错误不进排名（慢不等于弱）", "null", StatsBuilder.WeakScore(slow, 2.0, 20)?.ToString("0.###") ?? "null");

        KeyStat slowButWrong = new() { Vk = 90, Presses = 100, Typos = 3 };
        True("有错误时慢速会加分", StatsBuilder.WeakScore(slowButWrong, 2.0, 20) is > 0);

        KeyStat edit = new() { Vk = 8, Presses = 500, Typos = 50 };
        Eq("退格键不参与弱键排名", "0", StatsBuilder.WeakScore(edit, 1.0, 20)!.Value.ToString("0.###"));
    }

    private static void LabelExportTests()
    {
        Section("金标导出");

        AttributionResult result = new();
        for (int i = 0; i < 5; i++)
        {
            result.Records.Add(Rec("typo", 1000 + i * 100));
        }

        for (int i = 0; i < 45; i++)
        {
            result.Records.Add(Rec("rewording", 2000 + i * 100));
        }

        for (int i = 0; i < 10; i++)
        {
            result.Records.Add(Rec("blind", 8000 + i * 100));
        }

        System.Collections.Generic.List<LabelRow> rows = LabelExporter.Build(result, 20, 7, anonymize: false);
        Eq("条数按 --total 截断", "20", rows.Count.ToString());
        Eq("判错的全部进表", "5", rows.Count(r => r.Predicted).ToString());
        Eq("按时间排序", "1000", rows[0].Ts.ToString());
        Eq("抽样率 = 抽中/池子", "0.333", LabelExporter.SampleRate(result, 20).ToString("0.###"));
        False("盲区不进抽样池", rows.Any(r => r.Kind == "blind"));

        System.Collections.Generic.List<LabelRow> again = LabelExporter.Build(result, 20, 7, anonymize: false);
        True("同种子导两次一致", rows.Select(r => r.Ts).SequenceEqual(again.Select(r => r.Ts)));

        System.Collections.Generic.List<LabelRow> other = LabelExporter.Build(result, 20, 8, anonymize: false);
        False("换种子抽样不同", rows.Select(r => r.Ts).SequenceEqual(other.Select(r => r.Ts)));

        System.Collections.Generic.List<LabelRow> anon = LabelExporter.Build(result, 20, 7, anonymize: true);
        Eq("去标识后不留文字", string.Empty, anon[0].Word);
        Eq("去标识后不留应用", string.Empty, anon[0].Exe);
        Eq("去标识后不留时间", "0", anon[0].Ts.ToString());

        string csv = LabelExporter.ToCsv(rows);
        Eq("表头 11 列", "11", csv.Split("\r\n")[0].Split(',').Length.ToString());
        False("标注表不暴露机器判定", csv.Contains("机器判定"));
        True("含逗号的字段被引号包住", csv.Contains("\"abc,def\""));

        string key = LabelExporter.ToKeyCsv(rows);
        Eq("判定表 4 列", "4", key.Split("\r\n")[0].Split(',').Length.ToString());
        True("判定表带机器结论", key.Contains("是,"));

        Eq("换行可见化", "a\\nb", LabelExporter.Visible("a\nb"));
        Eq("空格可见化", "a·b", LabelExporter.Visible("a b"));
    }

    private static void KeybrExportTests()
    {
        Section("keybr 导出");

        AttributionResult day = new();
        KeyStat a = new() { Vk = 65, Presses = 100, Typos = 5 };
        a.Iki.AddRange([200, 300, 400]);
        KeyStat semicolon = new() { Vk = 186, Presses = 40, Typos = 9 };
        semicolon.Iki.AddRange([100]);
        KeyStat digit = new() { Vk = 50, Presses = 30, Typos = 2 };
        digit.Iki.AddRange([150, 250]);
        day.Keys[65] = a;
        day.Keys[186] = semicolon;
        day.Keys[50] = digit;
        day.Totals.TextActiveMs = 60000;

        System.Collections.Generic.List<(DateOnly, AttributionResult)> days = [(new DateOnly(2026, 11, 17), day)];
        string json = KeybrExport.Build(days);

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;
        Eq("顶层是数组", "Array", root.ValueKind.ToString());
        Eq("一天一个 session", "1", root.GetArrayLength().ToString());

        System.Text.Json.JsonElement s = root[0];
        Eq("layout 固定 en-us", "en-us", s.GetProperty("layout").GetString() ?? "");
        Eq("length = 字母数字击键数", "130", s.GetProperty("length").ToString());
        Eq("errors = 确认错误数", "7", s.GetProperty("errors").ToString());
        Eq("speed 按 字符/分钟", "130", s.GetProperty("speed").GetDouble().ToString("0"));

        System.Text.Json.JsonElement hist = s.GetProperty("histogram");
        Eq("符号键不进 histogram", "2", hist.GetArrayLength().ToString());
        Eq("按码位排序：数字在前", "50", hist[0].GetProperty("codePoint").ToString());
        Eq("数字的 timeToType 取中位数", "200", hist[0].GetProperty("timeToType").ToString());
        Eq("A 的 codePoint 是小写 a", "97", hist[1].GetProperty("codePoint").ToString());
        Eq("hitCount 取按下次数", "100", hist[1].GetProperty("hitCount").ToString());
        Eq("missCount 取确认错误", "5", hist[1].GetProperty("missCount").ToString());
        Eq("timeToType 取中位数", "300", hist[1].GetProperty("timeToType").ToString());

        AttributionResult empty = new();
        days = [(new DateOnly(2026, 11, 16), empty)];
        Eq("没有击键的天不导出", "[]", KeybrExport.Build(days).Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    private static void CsvExportTests()
    {
        Section("csv 导出");

        AttributionResult empty = new();
        string emptyCsv = CsvExport.Build(empty);
        string[] emptyLines = emptyCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Eq("空数据只输出表头", "1", emptyLines.Length.ToString());
        Eq("表头 15 列", "15", emptyLines[0].Split(',').Length.ToString());

        AttributionResult res = new()
        {
            Baseline = new Baseline { GlobalP50 = 200, PauseFactor = 3.0 },
        };
        KeyStat a = new() { Vk = 65, Presses = 40, Shifted = 5, Typos = 4, TyposUnreplaced = 1, PinyinTypos = 2 };
        a.Iki.AddRange([200, 300, 400]);
        a.Dwell.AddRange([80, 100]);
        KeyStat comma = new() { Vk = 188, Presses = 10, Typos = 1 }; // 样本不足 20，weakScore 应为空
        comma.Iki.Add(250);
        res.Keys[188] = comma;
        res.Keys[65] = a;

        string csv = CsvExport.Build(res, minKeySamples: 20);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Eq("导出表头加两行数据", "3", lines.Length.ToString());
        True("按 vk 升序，A(65) 在前", lines[1].StartsWith("65,A,letter,40,5,4,1,2,0.1,", StringComparison.Ordinal));
        True("A 有弱键分数且末尾非空", !lines[1].EndsWith(",", StringComparison.Ordinal));
        True("逗号键名被引号包住", lines[2].StartsWith("188,\",\",symbol,", StringComparison.Ordinal));
        True("样本不足时 weakScore 留空", lines[2].EndsWith(",", StringComparison.Ordinal));
    }

    private static void ShuangpinTests()
    {
        Section("小鹤双拼");

        // 官网「编码示例」里的那几组，逐个对
        Eq("双 shuang = u + l", "ul", ShuangpinCode.Of("shuang") ?? "");
        Eq("拼 pin = p + b", "pb", ShuangpinCode.Of("pin") ?? "");
        Eq("学 xue = x + t", "xt", ShuangpinCode.Of("xue") ?? "");
        Eq("中 zhong = v + s", "vs", ShuangpinCode.Of("zhong") ?? "");
        Eq("文 wen = w + f", "wf", ShuangpinCode.Of("wen") ?? "");
        Eq("位 wei = w + w", "ww", ShuangpinCode.Of("wei") ?? "");
        Eq("卷 juan = j + r", "jr", ShuangpinCode.Of("juan") ?? "");
        Eq("云 yun = y + y", "yy", ShuangpinCode.Of("yun") ?? "");

        // 零声母：单字母双写、双字母照全拼、三字母首字母+韵母键
        Eq("啊 a = aa", "aa", ShuangpinCode.Of("a") ?? "");
        Eq("爱 ai 照全拼", "ai", ShuangpinCode.Of("ai") ?? "");
        Eq("恩 en 照全拼", "en", ShuangpinCode.Of("en") ?? "");
        Eq("昂 ang = a + h", "ah", ShuangpinCode.Of("ang") ?? "");
        Eq("而 er 照全拼", "er", ShuangpinCode.Of("er") ?? "");

        Eq("去声调：lǜ -> lv", "lv", ShuangpinCode.StripTone("lǜ"));
        Eq("去声调：nǚ -> nv", "nv", ShuangpinCode.StripTone("nǚ"));
        Eq("去声调：zhōng -> zhong", "zhong", ShuangpinCode.StripTone("zhōng"));
        True("不认识的音节返回 null", ShuangpinCode.Of("zhuangq") is null);
    }

    private static void ZhSelectionTests()
    {
        Section("中文选字");

        True("内置字表已加载", HanziTable.All.Count > 3000);

        // 表里的码必须和 ShuangpinCode 现算的一致，否则说明生成脚本和代码口径漂了
        int mismatch = HanziTable.All.Count(e => ShuangpinCode.Of(e.Syllable) != e.Code);
        Eq("字表里的码与现算一致", "0", mismatch.ToString());

        HashSet<char> weak = ['c'];
        System.Collections.Generic.List<HanziEntry> picked = ZhSelector.Pick(weak, 40);
        Eq("生成指定数量", "40", picked.Count.ToString());
        True("大部分字含弱键", picked.Count(e => e.Code.Contains('c')) >= 30);
        Eq("不含弱键的约两成", "8", picked.Count(e => !e.Code.Contains('c')).ToString());

        // 命中两个弱键的应该排在只命中一个的前面
        HashSet<char> two = ['c', 'h'];
        System.Collections.Generic.List<HanziEntry> both = ZhSelector.Pick(two, 20);
        int twoHits = both.Count(e => e.Code.Count(two.Contains) == 2);
        True("有两个弱键的字排前面", twoHits > 0 && both[0].Code.Count(two.Contains) == 2);

        True("空弱键不出字", ZhSelector.Pick(new HashSet<char>(), 10).Count == 0);
        True("数量为 0 不出字", ZhSelector.Pick(weak, 0).Count == 0);
    }

    private static void SymbolSelectionTests()
    {
        Section("符号选词");

        HashSet<char> weak = ['{', '}', '[', ']'];
        System.Collections.Generic.List<string> picked = SymbolSelector.Pick(weak, 40);
        Eq("生成指定数量", "40", picked.Count.ToString());
        True("弱符号全部被覆盖", weak.All(w => picked.Any(p => p.Contains(w))));
        True("大部分串含弱符号", picked.Count(p => p.Any(weak.Contains)) >= 30);
        Eq("不含弱符号的约两成", "8", picked.Count(p => !p.Any(weak.Contains)).ToString());
        True("所有串不含内部空格", picked.All(p => !p.Any(char.IsWhiteSpace)));

        True("空弱符号不出串", SymbolSelector.Pick(new HashSet<char>(), 10).Count == 0);
        True("数量为 0 不出串", SymbolSelector.Pick(weak, 0).Count == 0);

        True("直接连写解析弱符号", SymbolSelector.ParseWeakSpec("{};[]").SetEquals(['{', '}', ';', '[', ']']));
        True("单独逗号保留", SymbolSelector.ParseWeakSpec(",").SetEquals([',']));
        True("逗号分隔时正确拆出符号", SymbolSelector.ParseWeakSpec("{},[]").SetEquals(['{', '}', '[', ']']));

        string statsJson = """
            {
              "symbols": [
                {"char":"{","vk":219,"shift":true,"count":30,"typos":4},
                {"char":";","vk":186,"shift":false,"count":50,"typos":2},
                {"char":"1","vk":49,"shift":false,"count":20,"typos":0}
              ],
              "keys": [
                {"vk":221,"name":"]","role":"symbol","weakScore":0.42}
              ]
            }
            """;
        System.Collections.Generic.List<char> fromStats = SymbolSelector.WeakSymbolsFromStatsJson(statsJson, limit: 6);
        Eq("stats 里错误最多的符号排第一", "{", fromStats[0].ToString());
        True("包含 symbols 有错的分号", fromStats.Contains(';'));
        True("包含 keys 弱符号键展开的 ] 和 }", fromStats.Contains(']') && fromStats.Contains('}'));
        True("空 stats 回落默认常用符号", SymbolSelector.WeakSymbolsFromStatsJson("{}", limit: 4).Count == 4);
    }

    private static void DashboardTests()
    {
        Section("看板");

        string html = Dashboard.Build("{\"totals\":{\"typos\":1}}");
        True("数据被内嵌", html.Contains("\"totals\""));
        False("占位符已替换", html.Contains("__DATA__"));
        False("不引用任何外部资源", html.Contains("http://") || html.Contains("https://"));
        False("没有外链脚本或样式", html.Contains("src=") || html.Contains("@import"));
        True("中文界面", html.Contains("打字画像"));
        True("包含准确率护栏提醒逻辑", html.Contains("准确率护栏提醒"));
        True("速度正常时不误报倍慢", html.Contains("速度正常"));
    }

    private static void AccuracyGuardTests()
    {
        Section("准确率护栏");

        AttributionResult empty = new();
        AccuracyReport emptyReport = AccuracyGuard.Evaluate(empty, 95.0);
        False("零击键不触发护栏", emptyReport.Triggered);
        Eq("零击键准确率为 100%", "100", emptyReport.AccuracyPercent.ToString("0"));

        AttributionResult healthy = new();
        healthy.Totals.Keystrokes = 100;
        healthy.Totals.Typos = 3;
        healthy.Keys[67] = new KeyStat { Vk = 67, Presses = 40, Typos = 3 };
        AccuracyReport healthyReport = AccuracyGuard.Evaluate(healthy, 95.0);
        False("97% 准确率不触发 95% 护栏", healthyReport.Triggered);
        Eq("健康准确率计算", "97", healthyReport.AccuracyPercent.ToString("0"));

        AttributionResult sloppy = new();
        sloppy.Totals.Keystrokes = 100;
        sloppy.Totals.Typos = 8; // 92% < 95%
        sloppy.Keys[90] = new KeyStat { Vk = 90, Presses = 20, Typos = 3 };
        sloppy.Keys[67] = new KeyStat { Vk = 67, Presses = 25, Typos = 5 };
        sloppy.Keys[86] = new KeyStat { Vk = 86, Presses = 15, Typos = 0 };
        AccuracyReport sloppyReport = AccuracyGuard.Evaluate(sloppy, 95.0);
        True("92% 准确率触发 95% 护栏", sloppyReport.Triggered);
        Eq("准确率 92%", "92", sloppyReport.AccuracyPercent.ToString("0"));
        Eq("错误键清单只收有错的键", "2", sloppyReport.TopErrorKeys.Count.ToString());
        Eq("错得最多的 C 排第一", "C", sloppyReport.TopErrorKeys[0].Name);
        Eq("Z 排第二", "Z", sloppyReport.TopErrorKeys[1].Name);
    }

    private static void DrillPageTests()
    {
        Section("练习页");

        string html = DrillPage.Build("[{\"c\":\"便\",\"p\":\"bian\",\"k\":\"bm\"}]", "测试练习 <script>");
        True("题目被内嵌", html.Contains("\"k\":\"bm\""));
        True("标题被替换", html.Contains("测试练习 &lt;script&gt;"));
        False("标题已转义", html.Contains("测试练习 <script>"));
        False("占位符已替换", html.Contains("__ITEMS__") || html.Contains("__TITLE__"));
        False("不引用任何外部资源", html.Contains("http://") || html.Contains("https://"));
        False("没有外链脚本或样式", html.Contains("src=") || html.Contains("@import"));
        True("练完能贴回结果", html.Contains("finishedAt"));
        True("使用 Tab 切换提示不抢占 h 键", html.Contains("e.key === 'Tab'") && !html.Contains("e.key === 'h'"));
        True("按错归因到目标键 want", html.Contains("stat(want).miss++") && !html.Contains("stat(ch).miss++"));
        True("键盘样式限定在 .krow .k 避免污染卡片", html.Contains(".krow .k{") && !html.Contains("\n.k{"));
        True("结算速度使用 correct 且防除零", html.Contains("elapsed > 0 ? Math.round(correct / (elapsed / 60000)) : 0"));
        True("结算卡片渲染单位", html.Contains("<span class=\"u\">"));
    }

    private static void DrillHistoryTests()
    {
        Section("练习回流");

        // 1. 解析校验
        False("非法 JSON 拒绝解析", DrillHistory.TryParse("{bad", out _, out _));
        False("缺少 keys 拒绝解析", DrillHistory.TryParse("{\"v\":1}", out _, out _));
        False("未知版本拒绝解析", DrillHistory.TryParse("{\"v\":2,\"keys\":[]}", out _, out _));
        False("非字母键拒绝解析", DrillHistory.TryParse("{\"v\":1,\"keys\":[{\"key\":\"1\",\"hit\":1,\"miss\":0}]}", out _, out _));

        string sample = """
            {"v":1,"finishedAt":"2026-11-20T10:00:00Z","total":20,"correct":18,"attempts":18,"elapsedMs":32000,"accuracy":85.0,"keys":[{"key":"C","hit":10,"miss":4},{"key":"V","hit":12,"miss":1}],"wrongChars":["好"]}
            """;
        True("合法结果解析成功", DrillHistory.TryParse(sample, out DrillRecord? parsed, out _));
        Eq("键名规范化为小写", "c", parsed!.Keys[0].Key.ToString());
        Eq("读取准确率", "85", parsed.Accuracy.ToString("0"));

        // 2. 落盘与重载
        string dir = Path.Combine(Path.GetTempPath(), "keytrace-drills-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string older = """
                {"v":1,"finishedAt":"2026-11-19T10:00:00Z","total":20,"correct":16,"attempts":16,"elapsedMs":40000,"accuracy":80.0,"keys":[{"key":"q","hit":10,"miss":4},{"key":"z","hit":25,"miss":0}],"wrongChars":["请"]}
                """;
            string newer = """
                {"v":1,"finishedAt":"2026-11-20T12:00:00Z","total":20,"correct":19,"attempts":19,"elapsedMs":28000,"accuracy":92.0,"keys":[{"key":"c","hit":10,"miss":4},{"key":"z","hit":25,"miss":0}],"wrongChars":["好"]}
                """;
            (string path1, _) = DrillHistory.Save(dir, older);
            (string path2, _) = DrillHistory.Save(dir, newer);
            True("记录文件落盘", File.Exists(path1) && File.Exists(path2));

            System.Collections.Generic.List<DrillRecord> recent = DrillHistory.LoadRecent(dir, 10);
            Eq("重载记录数量", "2", recent.Count.ToString());
            True("按完成时间倒序排列", recent[0].FinishedAt > recent[1].FinishedAt);

            // z 键累计 50 次命中且 0 错（已稳定），即使 statsWeak 里排第一也应退出
            System.Collections.Generic.List<char> rec = DrillHistory.RecommendWeakKeys(recent, ['z', 'p', 'x'], limit: 5);
            True("近期错得多的 c 排进推荐", rec.Contains('c'));
            True("旧场错得多的 q 也进推荐", rec.Contains('q'));
            True("最近一场的 c 排在旧场的 q 前面（同等错误率按时间衰减）", rec.IndexOf('c') < rec.IndexOf('q'));
            False("已稳定的 z 从推荐里退出", rec.Contains('z'));
            True("不足 5 个时用未稳定的 stats 弱键补齐", rec.Contains('p') && rec.Contains('x'));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void UserConfigTests()
    {
        Section("UserConfig");
        string dir = Path.Combine(Path.GetTempPath(), "keytrace-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            UserConfig def = UserConfig.Load(dir);
            False("默认关闭悬浮条", def.ShowMiniHud);
            True("默认开启 UIA 文本采集", def.CaptureText);
            False("默认显示明文", def.HideLiveText);
            True("默认不配置网页练习地址（与外部网页解耦）", def.WebPracticeUrl is null && !def.HasWebPracticeUrl);
            False("默认隐藏托盘网页练习入口", DesktopAppHost.ShouldShowWebPractice(def));
            True("默认自定义排除为空", def.ExtraExclusions.Count == 0);

            UserConfig custom = new()
            {
                ShowMiniHud = true,
                MiniHudLeft = 320,
                MiniHudTop = 180,
                HideLiveText = true,
                CaptureText = false,
                ImeOverride = "  shuangpin/ziranma ",
                ExtraExclusions = [" bank.exe ", "BANK.exe", "", "wechat.exe"],
                WebPracticeUrl = "  http://localhost:3000/keytrace  ",
            };
            custom.Save(dir);

            UserConfig reloaded = UserConfig.Load(dir);
            True("悬浮条状态往返", reloaded.ShowMiniHud);
            True("坐标往返", Math.Abs((reloaded.MiniHudLeft ?? 0) - 320) < 0.1 && Math.Abs((reloaded.MiniHudTop ?? 0) - 180) < 0.1);
            True("隐私模式往返", reloaded.HideLiveText);
            False("纯键码开关往返", reloaded.CaptureText);
            Eq("输入法方案往返并 trim", "shuangpin/ziranma", reloaded.ImeOverride ?? "");
            Eq("排除列表去重与清理空白", "2", reloaded.ExtraExclusions.Count.ToString());
            Eq("网页地址往返并 trim", "http://localhost:3000/keytrace", reloaded.WebPracticeUrl ?? "");
            True("配置网页地址后启用托盘入口", DesktopAppHost.ShouldShowWebPractice(reloaded));

            // 空白网页地址保存/读取后应规范化为 null
            reloaded.WebPracticeUrl = "   ";
            reloaded.Save(dir);
            UserConfig cleared = UserConfig.Load(dir);
            True("空白网页地址规范化为 null", cleared.WebPracticeUrl is null && !cleared.HasWebPracticeUrl);

            // 兼容缺少字段或小驼峰 webPracticeUrl 的手写 JSON
            File.WriteAllText(UserConfig.FilePath(dir), "{}");
            True("缺失 webPracticeUrl 字段安全读取为 null", UserConfig.Load(dir).WebPracticeUrl is null);

            File.WriteAllText(UserConfig.FilePath(dir), "{\"webPracticeUrl\": \"   \"}");
            True("小驼峰空 webPracticeUrl 规范化为 null", UserConfig.Load(dir).WebPracticeUrl is null);

            File.WriteAllText(UserConfig.FilePath(dir), "{\"webPracticeUrl\": \" https://example.com/practice \"}");
            Eq("小驼峰 webPracticeUrl 正常读取并 trim", "https://example.com/practice", UserConfig.Load(dir).WebPracticeUrl ?? "");

            // 损坏的 JSON 应安全回落默认值
            File.WriteAllText(UserConfig.FilePath(dir), "{ not valid json !!!");
            UserConfig broken = UserConfig.Load(dir);
            False("损坏 JSON 回落默认悬浮条设置", broken.ShowMiniHud);
            True("损坏 JSON 回落默认开启文本采集", broken.CaptureText);
            True("损坏 JSON 回落默认无网页地址", broken.WebPracticeUrl is null);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void StandaloneDashboardAndTrayTests()
    {
        Section("StandaloneDashboardAndTray");

        // 1. 托盘菜单：默认（未配置网页地址）不显示"打开网页练习"，始终显示"查看统计看板"
        bool dashboardClicked = false;
        bool webClicked = false;
        using (System.Windows.Forms.ContextMenuStrip standaloneMenu = TrayApplication.BuildContextMenu(
            new System.Windows.Forms.ToolStripMenuItem("status"),
            new System.Windows.Forms.ToolStripMenuItem("main"),
            new System.Windows.Forms.ToolStripMenuItem("hud"),
            new System.Windows.Forms.ToolStripMenuItem("pause"),
            showWebPractice: false,
            onOpenDashboard: () => dashboardClicked = true,
            onOpenWebPractice: () => webClicked = true,
            onExit: () => { }))
        {
            System.Collections.Generic.List<string?> texts = [.. standaloneMenu.Items
                .Cast<System.Windows.Forms.ToolStripItem>()
                .Select(i => i.Text)];
            True("独立模式托盘包含查看统计看板", texts.Contains(TrayApplication.OpenDashboardMenuText));
            False("独立模式托盘隐藏打开网页练习", texts.Contains(TrayApplication.OpenWebPracticeMenuText));

            System.Windows.Forms.ToolStripItem dashItem = standaloneMenu.Items
                .Cast<System.Windows.Forms.ToolStripItem>()
                .First(i => i.Text == TrayApplication.OpenDashboardMenuText);
            dashItem.PerformClick();
            True("点击查看统计看板触发回调", dashboardClicked);
            False("独立模式不触发网页练习回调", webClicked);
        }

        // 2. 托盘菜单：配置 WebPracticeUrl 后显示"打开网页练习"且可触发
        using (System.Windows.Forms.ContextMenuStrip linkedMenu = TrayApplication.BuildContextMenu(
            new System.Windows.Forms.ToolStripMenuItem("status"),
            new System.Windows.Forms.ToolStripMenuItem("main"),
            new System.Windows.Forms.ToolStripMenuItem("hud"),
            new System.Windows.Forms.ToolStripMenuItem("pause"),
            showWebPractice: true,
            onOpenDashboard: () => { },
            onOpenWebPractice: () => webClicked = true,
            onExit: () => { }))
        {
            System.Collections.Generic.List<string?> texts = [.. linkedMenu.Items
                .Cast<System.Windows.Forms.ToolStripItem>()
                .Select(i => i.Text)];
            True("配置网页地址时托盘保留查看统计看板", texts.Contains(TrayApplication.OpenDashboardMenuText));
            True("配置网页地址时托盘显示打开网页练习", texts.Contains(TrayApplication.OpenWebPracticeMenuText));

            System.Windows.Forms.ToolStripItem webItem = linkedMenu.Items
                .Cast<System.Windows.Forms.ToolStripItem>()
                .First(i => i.Text == TrayApplication.OpenWebPracticeMenuText);
            webItem.PerformClick();
            True("点击打开网页练习触发回调", webClicked);
        }

        // 3. 独立一键生成并打开本地离线统计看板（含空数据、近 7 天数据、历史回退、异常兜底）
        string baseDir = Path.Combine(Path.GetTempPath(), "keytrace-dash-" + Guid.NewGuid().ToString("N")[..8]);
        string eventsDir = Path.Combine(baseDir, "events");
        try
        {
            DateOnly today = new(2026, 11, 25);

            // 3a. 空目录不崩溃，生成合法空看板并给出提示
            bool flushed = false;
            string? openedPath = null;
            string? notified = null;
            DashboardGenerationResult emptyRes = DesktopAppHost.OpenStandaloneDashboard(
                eventsDir,
                baseDir,
                flushEvents: () => flushed = true,
                openPath: p => openedPath = p,
                notify: m => notified = m,
                todayOverride: today);
            True("空事件目录生成看板不崩溃", emptyRes.Success);
            True("生成前调用落盘刷新", flushed);
            Eq("空事件目录事件数为 0", "0", emptyRes.EventCount.ToString());
            Eq("空看板路径与打开路径一致", Dashboard.ReportFilePath(baseDir), openedPath ?? "");
            True("空看板已落盘", File.Exists(openedPath!));
            True("空事件目录给出友好提示", !string.IsNullOrEmpty(notified));
            string emptyHtml = File.ReadAllText(openedPath!);
            True("空看板为完整离线 HTML", emptyHtml.Contains("打字画像") && !emptyHtml.Contains("__DATA__"));
            False("空看板不含任何外链", emptyHtml.Contains("http://") || emptyHtml.Contains("https://"));

            // 3b. 有近期事件时汇总并写入真实统计
            Directory.CreateDirectory(eventsDir);
            long t0 = new DateTimeOffset(today.ToDateTime(new TimeOnly(9, 30))).ToUnixTimeMilliseconds();
            System.Collections.Generic.List<string> lines = [];
            for (int i = 0; i < 6; i++)
            {
                lines.Add(new KeyEvent
                {
                    Ts = t0 + i * 200,
                    Mono = 1000 + i * 200,
                    Ev = "kd",
                    Vk = 'A' + i,
                    Sc = 30,
                    Mods = 0,
                    Exe = "notepad.exe",
                    Sid = "s1",
                }.ToJsonLine());
            }

            File.WriteAllLines(Path.Combine(eventsDir, $"{today:yyyy-MM-dd}.jsonl"), lines);

            openedPath = null;
            notified = null;
            DashboardGenerationResult populatedRes = DesktopAppHost.OpenStandaloneDashboard(
                eventsDir,
                baseDir,
                flushEvents: null,
                openPath: p => openedPath = p,
                notify: m => notified = m,
                todayOverride: today);
            True("有近期事件时成功生成看板", populatedRes.Success && populatedRes.EventCount == 6);
            False("有近期事件时不回退全部历史", populatedRes.UsedAllHistory);
            True("有近期事件时不弹空数据提醒", notified is null);
            string populatedHtml = File.ReadAllText(openedPath!);
            True("看板内嵌实际应用数据", populatedHtml.Contains("notepad.exe"));

            // 3c. 近 7 天无事件但更早有历史数据时自动回退全部历史
            File.Delete(Path.Combine(eventsDir, $"{today:yyyy-MM-dd}.jsonl"));
            DateOnly olderDay = today.AddDays(-20);
            long olderTs = new DateTimeOffset(olderDay.ToDateTime(new TimeOnly(14, 0))).ToUnixTimeMilliseconds();
            File.WriteAllLines(Path.Combine(eventsDir, $"{olderDay:yyyy-MM-dd}.jsonl"), [
                new KeyEvent
                {
                    Ts = olderTs,
                    Mono = 1000,
                    Ev = "kd",
                    Vk = 'K',
                    Sc = 37,
                    Mods = 0,
                    Exe = "code.exe",
                    Sid = "s-old",
                }.ToJsonLine(),
            ]);

            DashboardGenerationResult historyRes = DesktopAppHost.OpenStandaloneDashboard(
                eventsDir,
                baseDir,
                flushEvents: null,
                openPath: _ => { },
                notify: _ => { },
                todayOverride: today);
            True("近 7 天为空时自动回退全部历史", historyRes.Success && historyRes.UsedAllHistory && historyRes.EventCount == 1);

            // 3d. 输出目标被文件占用导致无法写入时优雅报错、不抛异常、不打开浏览器
            string blockedBase = Path.Combine(baseDir, "not-a-directory");
            File.WriteAllText(blockedBase, "blocker");
            bool openedOnFail = false;
            string? failNotice = null;
            DashboardGenerationResult failRes = DesktopAppHost.OpenStandaloneDashboard(
                eventsDir,
                blockedBase,
                flushEvents: null,
                openPath: _ => openedOnFail = true,
                notify: m => failNotice = m,
                todayOverride: today);
            False("写入失败时返回 Success=false 且不崩溃", failRes.Success);
            False("写入失败时不尝试打开浏览器", openedOnFail);
            True("写入失败时通过托盘提示错误", !string.IsNullOrEmpty(failNotice));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    private static void ExclusionListTests()
    {
        Section("ExclusionList");
        ExclusionList list = new(["customApp.exe"]);
        True("内置密码管理器始终被排除", list.IsExcluded("1password.exe"));
        True("自定义排除忽略大小写", list.IsExcluded("CUSTOMAPP.EXE"));

        list.SetExtra(["bank.exe", "1password.exe", "vault.exe", "BANK.EXE"]);
        True("新增自定义进程生效", list.IsExcluded("bank.exe"));
        True("新增第二个进程生效", list.IsExcluded("Vault.EXE"));
        False("被移除的旧进程不再排除", list.IsExcluded("customApp.exe"));
        True("更换列表后内置仍受保护", list.IsExcluded("keepassxc.exe"));
        Eq("ExtraNames 不重复计入内置项", "2", list.ExtraNames.Count.ToString());
    }

    private static void LiveTelemetryTests()
    {
        Section("LiveTelemetry");
        LiveTelemetry t = new();
        long baseTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000;

        t.SeedFromTodayCount(100);

        // 修饰键和 Ctrl 快捷键不计入今日击键
        t.Observe(new KeyEvent { Ts = baseTs, Ev = "kd", Vk = 16, Sc = 42, Mods = 1, Exe = "code.exe", Sid = "s1" });
        t.Observe(new KeyEvent { Ts = baseTs + 50, Ev = "kd", Vk = 'C', Sc = 46, Mods = 2, Exe = "code.exe", Sid = "s1" });
        Eq("修饰键和快捷键不递增计数", "100", t.Snapshot(baseTs + 100).TodayKeystrokes.ToString());

        // 连续 5 下按 200ms 间隔 = 60 wpm；中间插一个 >3s 停顿再打 4 下依然算 ~60 wpm
        long ts = baseTs;
        for (int i = 0; i < 5; i++)
        {
            ts += 200;
            t.Observe(new KeyEvent { Ts = ts, Ev = "kd", Vk = 'A' + i, Sc = 30, Mods = 0, Exe = "code.exe", Sid = "s1" });
        }

        // >3000ms 停顿（实际跳过 5000ms）
        ts += 5000;
        t.Observe(new KeyEvent { Ts = ts, Ev = "kd", Vk = 'O', Sc = 24, Mods = 0, Exe = "code.exe", Sid = "s1" });
        // 退格改按 W（在 3s 内触发实时纠错捕获）
        t.Observe(new KeyEvent { Ts = ts + 180, Ev = "kd", Vk = 8, Sc = 14, Mods = 0, Exe = "code.exe", Sid = "s1" });
        t.Observe(new KeyEvent { Ts = ts + 360, Ev = "kd", Vk = 'W', Sc = 17, Mods = 0, Exe = "code.exe", Sid = "s1" });

        LiveTelemetrySnapshot snapActive = t.Snapshot(ts + 500);
        True($"实时 WPM 合理 ({snapActive.LiveWpm})", snapActive.LiveWpm is >= 45 and <= 80);
        Eq("实时捕获到纠错文案", "误 O → W", snapActive.LastCorrectionText ?? "");
        Eq("紧凑纠错徽标格式正确", "误 O→W", snapActive.LastCorrectionCompact ?? "");

        // 超过 8 秒纠错提醒应消失，超过 15 秒空闲 WPM 归零
        LiveTelemetrySnapshot snapIdle = t.Snapshot(ts + 20_000);
        True("超过 8 秒后纠错提醒自动消除", snapIdle.LastCorrectionText is null);
        Eq("超过 15 秒空闲速度归零", "0", snapIdle.LiveWpm.ToString());

        // 批量粘贴 / URL 过滤，正常上屏进入心跳，且总条数受限于 80
        t.Observe(new KeyEvent { Ts = ts + 400, Ev = "commit", Text = "粘贴词", Keys = 0, Exe = "code.exe", Sid = "s1" });
        t.Observe(new KeyEvent { Ts = ts + 450, Ev = "commit", Text = "https://example.com", Keys = 3, Exe = "code.exe", Sid = "s1" });
        t.Observe(new KeyEvent { Ts = ts + 500, Ev = "commit", Text = "你好世界", Keys = 4, Exe = "code.exe", Sid = "s1" });

        LiveTelemetrySnapshot afterCommit = t.Snapshot(ts + 600);
        True("过滤零击键和 URL 后只保留真实上屏与纠错", afterCommit.Feed.Count == 2);
        Eq("最新心跳为真实上屏文字", "你好世界", afterCommit.Feed[0].Text ?? "");

        for (int i = 0; i < 100; i++)
        {
            t.Observe(new KeyEvent { Ts = ts + 700 + i, Ev = "commit", Text = $"字{i}", Keys = 2, Exe = "code.exe", Sid = "s1" });
        }

        Eq("心跳流严格截断在 80 条以内", "80", t.Snapshot(ts + 900).Feed.Count.ToString());

        // 分时段增量桶统计与 SeedFromTodayCount 截断去重
        LiveTelemetry hourly = new();
        DateOnly localToday = DateOnly.FromDateTime(DateTime.Now);
        long t10a = new DateTimeOffset(localToday.ToDateTime(new TimeOnly(10, 5))).ToUnixTimeMilliseconds();
        long t10b = t10a + 200;
        long t14 = new DateTimeOffset(localToday.ToDateTime(new TimeOnly(14, 15))).ToUnixTimeMilliseconds();
        hourly.Observe(new KeyEvent { Ts = t10a, Ev = "kd", Vk = 'A', Sc = 30, Mods = 0, Exe = "code.exe", Sid = "s1" });
        hourly.Observe(new KeyEvent { Ts = t10b, Ev = "kd", Vk = 'B', Sc = 48, Mods = 0, Exe = "code.exe", Sid = "s1" });
        hourly.Observe(new KeyEvent { Ts = t14, Ev = "kd", Vk = 'C', Sc = 46, Mods = 0, Exe = "code.exe", Sid = "s1" });

        LiveTelemetrySnapshot hBeforeSeed = hourly.Snapshot(t14 + 500);
        Eq("LiveHourlyDelta 长度为 24", "24", hBeforeSeed.LiveHourlyDelta.Count.ToString());
        Eq("10 点增量落入 10 时桶", "2", hBeforeSeed.LiveHourlyDelta[10].ToString());
        Eq("14 点增量落入 14 时桶", "1", hBeforeSeed.LiveHourlyDelta[14].ToString());

        hourly.SeedFromTodayCount(50, upToTs: t10b);
        LiveTelemetrySnapshot hAfterSeed = hourly.Snapshot(t14 + 600);
        Eq("Seed 截断后总计数不重复累计", "51", hAfterSeed.TodayKeystrokes.ToString());
        Eq("Seed 截断点之前的 10 时增量清零", "0", hAfterSeed.LiveHourlyDelta[10].ToString());
        Eq("Seed 截断点之后的 14 时增量保留", "1", hAfterSeed.LiveHourlyDelta[14].ToString());
    }

    private static void DesktopStatsServiceTests()
    {
        Section("DesktopStatsService");
        string dir = Path.Combine(Path.GetTempPath(), "keytrace-dstats-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            DateOnly today = DateOnly.FromDateTime(DateTime.Now);
            long t0 = new DateTimeOffset(today.ToDateTime(new TimeOnly(10, 0))).ToUnixTimeMilliseconds();
            long mono = 1000;
            System.Collections.Generic.List<string> lines = [];

            string JsonLine(long ts, long m, string ev, int vk, int sc, string exe, string sid, string? text = null, int? keys = null)
            {
                return new KeyEvent
                {
                    Ts = ts,
                    Mono = m,
                    Ev = ev,
                    Vk = vk,
                    Sc = sc,
                    Mods = 0,
                    Exe = exe,
                    Sid = sid,
                    Text = text,
                    Src = text is null ? null : (ev == "meta" ? "config" : "uia"),
                    Keys = keys,
                }.ToJsonLine();
            }

            lines.Add(JsonLine(t0, mono, "meta", 0, 0, "keytrace", "s0", "ime=shuangpin/xiaohe"));
            // 25 次基线与常见按键 + Q 键纠错
            for (int i = 0; i < 24; i++)
            {
                t0 += 180;
                mono += 180;
                int vk = 'A' + (i % 6);
                lines.Add(JsonLine(t0, mono, "kd", vk, 30, "msedge.exe", "s1"));
            }

            // 模拟对 Q 键的多次误按并改按 W
            for (int i = 0; i < 3; i++)
            {
                t0 += 200; mono += 200;
                lines.Add(JsonLine(t0, mono, "kd", 'Q', 16, "msedge.exe", "s1"));
                t0 += 150; mono += 150;
                lines.Add(JsonLine(t0, mono, "kd", 8, 14, "msedge.exe", "s1"));
                t0 += 160; mono += 160;
                lines.Add(JsonLine(t0, mono, "kd", 'W', 17, "msedge.exe", "s1"));
            }

            t0 += 100; mono += 100;
            lines.Add(JsonLine(t0, mono, "commit", 0, 0, "msedge.exe", "s1", "测试上屏文本", 8));

            string todayFile = Path.Combine(dir, $"{today:yyyy-MM-dd}.jsonl");
            File.WriteAllLines(todayFile, lines);

            DesktopSnapshot snap = DesktopStatsService.LoadCore(dir, StatsRange.Today, null);
            True("统计识别出今日输入法方案", snap.ImeName == "shuangpin/xiaohe");
            True($"统计有总击键 ({snap.Keystrokes})", snap.Keystrokes >= 27);
            True($"识别出弱键 Q ({snap.WeakLetters.Count})", snap.WeakLetters.Any(k => k.Name == "Q"));
            True("记录最近上屏片段", snap.RecentCommits.Any(c => c.Text == "测试上屏文本"));
            True("分布到应用 msedge.exe", snap.Apps.Any(a => a.Exe == "msedge.exe"));
            True("24 小时数组长度正确", snap.HourlySeries.Count == 24 && snap.HourlySeries[10].Keystrokes > 0);
            True("最近纠错还原含 Q -> W", snap.RecentTypos.Any(r => r.Deleted == 'q' && r.Replacement == 'w'));

            TodayCountsSnapshot counts = DesktopStatsService.LoadTodayCountsCore(dir);
            Eq("LoadTodayCountsCore 总数与 LoadCore 一致", snap.TodayKeystrokesFromDisk.ToString(), counts.TotalKeystrokes.ToString());
            Eq("LoadTodayCountsCore 10 时桶一致", snap.HourlySeries[10].Keystrokes.ToString(), counts.HourlyKeystrokes[10].ToString());
            Eq("LoadTodayCountsCore 其他时桶为 0", "0", counts.HourlyKeystrokes[15].ToString());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void ControlSignalsTests()
    {
        Section("ControlSignals");
        string dir = Path.Combine(Path.GetTempPath(), "keytrace-sig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            False("初始无唤醒文件", ControlSignals.ConsumeShowTrigger(dir));

            string triggerPath = ControlSignals.ShowTriggerPath(dir);
            File.WriteAllText(triggerPath, "123");
            True("首次消费唤醒文件返回 true", ControlSignals.ConsumeShowTrigger(dir));
            False("消费后唤醒文件被自动清除", File.Exists(triggerPath));
            False("二次消费返回 false", ControlSignals.ConsumeShowTrigger(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void AppIconsTests()
    {
        Section("AppIcons");
        string[] names = typeof(AppIcons).Assembly.GetManifestResourceNames();
        True("嵌入录制中 .ico 资源存在", names.Contains(AppIcons.ActiveResourceName));
        True("嵌入暂停态 .ico 资源存在", names.Contains(AppIcons.PausedResourceName));

        using System.Drawing.Icon activeTray = AppIcons.LoadTrayIcon(paused: false);
        using System.Drawing.Icon pausedTray = AppIcons.LoadTrayIcon(paused: true);
        True($"录制态托盘图标尺寸有效 ({activeTray.Width}x{activeTray.Height})", activeTray.Width >= 16 && activeTray.Height >= 16);
        True($"暂停态托盘图标尺寸有效 ({pausedTray.Width}x{pausedTray.Height})", pausedTray.Width >= 16 && pausedTray.Height >= 16);

        System.Windows.Media.ImageSource? winIcon = AppIcons.LoadWindowIcon(paused: false);
        System.Windows.Media.Imaging.BitmapSource? bmp = winIcon as System.Windows.Media.Imaging.BitmapSource;
        True($"窗口图标选用高分辨率帧 ({bmp?.PixelWidth}px)", bmp is not null && bmp.PixelWidth >= 128);
    }

    private static void ImeConfigTests()
    {
        Section("ImeConfig");
        Eq("关闭双拼返回 full-pinyin", "full-pinyin", ImeConfig.DescribeFromValues(false, 0, _ => "小鹤双拼*2"));
        Eq("内置 0 -> 微软双拼", "shuangpin/microsoft", ImeConfig.DescribeFromValues(true, 0, _ => null));
        Eq("内置 1 -> 智能ABC", "shuangpin/abc", ImeConfig.DescribeFromValues(true, 1, _ => null));
        Eq("内置 2 -> 紫光双拼", "shuangpin/ziguang", ImeConfig.DescribeFromValues(true, 2, _ => null));
        Eq("内置 3 -> 自然码", "shuangpin/ziranma", ImeConfig.DescribeFromValues(true, 3, _ => null));
        Eq("内置 4 -> 中文之星", "shuangpin/chinese-star", ImeConfig.DescribeFromValues(true, 4, _ => null));
        Eq("内置 5 -> 大牛", "shuangpin/daniu", ImeConfig.DescribeFromValues(true, 5, _ => null));
        Eq("切换回内置微软时不误报残留自定义槽 0", "shuangpin/microsoft",
            ImeConfig.DescribeFromValues(true, 0, slot => slot == 0 ? "小鹤双拼*2*^*iuvdjhcwfg" : null));
        Eq("自定义槽 10 小鹤", "shuangpin/xiaohe",
            ImeConfig.DescribeFromValues(true, 10, slot => slot == 0 ? "小鹤双拼*2*^*iuvdjhcwfg" : null));
        Eq("自定义槽 11 搜狗", "shuangpin/sogou",
            ImeConfig.DescribeFromValues(true, 11, slot => slot == 1 ? "搜狗双拼" : null));
        Eq("自定义槽 10 自然码", "shuangpin/ziranma",
            ImeConfig.DescribeFromValues(true, 10, slot => slot == 0 ? "Ziranma" : null));
        Eq("未知自定义名称保留槽号", "shuangpin/custom-2",
            ImeConfig.DescribeFromValues(true, 12, slot => slot == 2 ? "MyLayout" : null));
        Eq("无 scheme 键时回退检查自定义槽或微软双拼", "shuangpin/microsoft",
            ImeConfig.DescribeFromValues(true, null, _ => null));
    }

    private static TypoRecord Rec(string kind, long ts) => new()
    {
        Kind = kind,
        Ts = ts,
        Word = "abc,def",
        Deleted = ',',
        Key = ",",
        RunLen = 1,
    };

    private static AttributionResult Analyze(params EventRecord[] scenario) => Analyze(true, scenario);
    private static AttributionResult Analyze(bool textVisible, params EventRecord[] scenario)
    {
        System.Collections.Generic.List<EventRecord> events = [];
        for (int i = 0; i < 20; i++)
        {
            events.Add(new EventRecord(
                1, 100 + i * 200, 100 + i * 200, "kd", 65 + (i % 26), 30, 0, "base.exe", "base", null, null, null));
        }

        // 基线事件占 100–3900，场景整体后移，避免两条时间线交叉
        foreach (EventRecord e in scenario)
        {
            events.Add(e with { Ts = e.Ts + 5000, Mono = e.Mono + 5000 });
        }

        // 会话里必须有一条读得到文字的 commit，否则整个会话按"盲区"处理，退格一律不判错
        if (textVisible)
        {
            events.Add(new EventRecord(
                1, 1_700_000_007_900, 7_900, "commit", 0, 0, 0, "a.exe", "s1", "x", "uia", 1));
        }

        return new Attributor().Run(events);
    }

    private static EventRecord Kd(long mono, int vk) =>
        new(1, 1_700_000_000_000 + mono, mono, "kd", vk, 30, 0, "a.exe", "s1", null, null, null);

    private static EventRecord Comp(long mono, string text) =>
        new(1, 1_700_000_000_000 + mono, mono, "comp", 0, 0, 0, "a.exe", "s1", text, "uia", text.Length);

    private static EventRecord Commit(long mono, string text, int keys) =>
        new(1, 1_700_000_000_000 + mono, mono, "commit", 0, 0, 0, "a.exe", "s1", text, "uia", keys);

    private static void Section(string name) => Console.WriteLine($"[{name}]");
    private static void Eq(string name, string expected, string actual)
    {
        if (expected == actual)
        {
            Pass(name);
            return;
        }

        Fail($"{name}：期望 \"{expected}\"，实际 \"{actual}\"");
    }

    private static void True(string name, bool condition)
    {
        if (condition)
        {
            Pass(name);
        }
        else
        {
            Fail(name);
        }
    }

    private static void False(string name, bool condition) => True(name, !condition);

    private static void Pass(string name)
    {
        _passed++;
        Console.WriteLine($"  ok   {name}");
    }

    private static void Fail(string name)
    {
        _failed++;
        Console.WriteLine($"  FAIL {name}");
    }
}
