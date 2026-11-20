using System;
using System.IO;
using System.Linq;
using KeyTrace.Analysis;
using KeyTrace.Collect;

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
        AccuracyGuardTests();
        DashboardTests();
        DrillPageTests();

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

        string html = DrillPage.Build("[{\"c\":\"便\",\"p\":\"bian\",\"k\":\"bm\"}]", "测试练习");
        True("题目被内嵌", html.Contains("\"k\":\"bm\""));
        True("标题被替换", html.Contains("测试练习"));
        False("占位符已替换", html.Contains("__ITEMS__") || html.Contains("__TITLE__"));
        False("不引用任何外部资源", html.Contains("http://") || html.Contains("https://"));
        False("没有外链脚本或样式", html.Contains("src=") || html.Contains("@import"));
        True("练完能贴回结果", html.Contains("finishedAt"));
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
