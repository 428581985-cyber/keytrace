using System;
using Microsoft.Win32;

namespace KeyTrace.Collect;

/// <summary>
/// 读输入法方案。全拼/双拼不是运行时状态，是配置，写在注册表里，启动时读一次就够。
/// 中/英的切换是运行时状态，那个拿不到（微软拼音走 TSF，没有 IMM32 上下文）。
/// </summary>
internal static class ImeConfig
{
    private const string ChsKey = @"Software\Microsoft\InputMethod\Settings\CHS";

    public static string Describe()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ChsKey);
            if (key is null)
            {
                return "unknown";
            }

            bool isDoublePinyin = key.GetValue("Enable Double Pinyin") is int flag && flag != 0;
            int? scheme = key.GetValue("DoublePinyinScheme") switch
            {
                int i => i,
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => null,
            };
            return DescribeFromValues(
                isDoublePinyin,
                scheme,
                slot => key.GetValue($"UserDefinedDoublePinyinScheme{slot}")?.ToString());
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    internal static string DescribeFromValues(
        bool isDoublePinyin,
        int? scheme,
        Func<int, string?> readCustomScheme)
    {
        if (!isDoublePinyin)
        {
            return "full-pinyin";
        }

        if (scheme is int schemeId)
        {
            return schemeId switch
            {
                0 => "shuangpin/microsoft",
                1 => "shuangpin/abc",
                2 => "shuangpin/ziguang",
                3 => "shuangpin/ziranma",
                4 => "shuangpin/chinese-star",
                5 => "shuangpin/daniu",
                >= 10 => MatchCustomScheme(
                    readCustomScheme(schemeId - 10) ?? (schemeId != 10 ? readCustomScheme(0) : null),
                    $"shuangpin/custom-{schemeId - 10}"),
                _ => $"shuangpin/scheme-{schemeId}",
            };
        }

        return MatchCustomScheme(readCustomScheme(0), "shuangpin/microsoft");
    }

    private static string MatchCustomScheme(string? custom, string fallback)
    {
        if (string.IsNullOrWhiteSpace(custom))
        {
            return fallback;
        }
        if (custom.Contains("小鹤", StringComparison.Ordinal) ||
            custom.Contains("xiaohe", StringComparison.OrdinalIgnoreCase) ||
            custom.Contains("flypy", StringComparison.OrdinalIgnoreCase))
        {
            return "shuangpin/xiaohe";
        }
        if (custom.Contains("自然码", StringComparison.Ordinal) ||
            custom.Contains("ziranma", StringComparison.OrdinalIgnoreCase) ||
            custom.Contains("zrm", StringComparison.OrdinalIgnoreCase))
        {
            return "shuangpin/ziranma";
        }
        if (custom.Contains("搜狗", StringComparison.Ordinal) ||
            custom.Contains("sogou", StringComparison.OrdinalIgnoreCase))
        {
            return "shuangpin/sogou";
        }
        if (custom.Contains("微软", StringComparison.Ordinal) ||
            custom.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
            custom.Contains("mspy", StringComparison.OrdinalIgnoreCase))
        {
            return "shuangpin/microsoft";
        }
        return fallback;
    }
}
