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
        Eq("没有 comp 的退格算普通 typo", "1", noComp.Totals.Typos.ToString());
        Eq("compCoverage 只统计到 1 条", "0", noComp.Chinese.CommitsWithComp.ToString());

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
            new EventRecord(1, 3000, 3000, "kd", 80, 25, 2, "a.exe", "s1", null, null, null));
        Eq("带 Ctrl 的按键不算打字", "1", shortcut.Skipped.Shortcut.ToString());
    }

    private static void WeakScoreTests()
    {
        Section("弱键评分");
        KeyStat few = new() { Vk = 90, Presses = 5, Typos = 2 };
        Eq("样本不足不排名", "null", StatsBuilder.WeakScore(few, 1.0, 20)?.ToString("0.###") ?? "null");

        KeyStat bad = new() { Vk = 90, Presses = 100, Typos = 15 };
        Eq("错误率拉满时只看速度项", "0.7", StatsBuilder.WeakScore(bad, 1.0, 20)!.Value.ToString("0.###"));

        KeyStat slow = new() { Vk = 90, Presses = 100, Typos = 0 };
        True("速度慢也应当有分", StatsBuilder.WeakScore(slow, 2.0, 20) is > 0);

        KeyStat edit = new() { Vk = 8, Presses = 500, Typos = 50 };
        Eq("退格键不参与弱键排名", "0", StatsBuilder.WeakScore(edit, 1.0, 20)!.Value.ToString("0.###"));
    }

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
