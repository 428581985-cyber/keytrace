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
            if (!isDoublePinyin)
            {
                return "full-pinyin";
            }

            string custom = key.GetValue("UserDefinedDoublePinyinScheme0")?.ToString() ?? string.Empty;
            if (custom.Contains("小鹤", StringComparison.Ordinal))
            {
                return "shuangpin/xiaohe";
            }

            if (custom.Contains("自然码", StringComparison.Ordinal))
            {
                return "shuangpin/ziranma";
            }

            object? scheme = key.GetValue("DoublePinyinScheme");
            return $"shuangpin/scheme-{scheme}";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
