using System;
using System.IO;
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
