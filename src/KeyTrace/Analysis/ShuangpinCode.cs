using System.Text;

namespace KeyTrace.Analysis;

/// <summary>
/// 小鹤双拼：音节 → 两个键。
///
/// 规则出处是官网（flypy.cc）「零声母」一节，不是二手项目的实现：
/// 单字母韵母双写（啊 aa）、双字母韵母照全拼（爱 ai、恩 en）、三字母取首字母 + 韵母键（昂 ah）。
/// 网上有几个开源项目把 ai 写成 ad、en 写成 ef，那是照微软双拼的零声母规则套过来的，不是小鹤。
/// </summary>
internal static class ShuangpinCode
{
    /// <summary>声母 → 键。zh/ch/sh 各占一个键；y/w 按普通声母处理。</summary>
    private static readonly Dictionary<string, char> Initials = new()
    {
        ["zh"] = 'v', ["ch"] = 'i', ["sh"] = 'u',
        ["b"] = 'b', ["p"] = 'p', ["m"] = 'm', ["f"] = 'f',
        ["d"] = 'd', ["t"] = 't', ["n"] = 'n', ["l"] = 'l',
        ["g"] = 'g', ["k"] = 'k', ["h"] = 'h',
        ["j"] = 'j', ["q"] = 'q', ["x"] = 'x',
        ["r"] = 'r', ["z"] = 'z', ["c"] = 'c', ["s"] = 's',
        ["y"] = 'y', ["w"] = 'w',
    };

    /// <summary>韵母 → 键。同一键上的几个韵母靠声母消歧，不需要额外规则。</summary>
    private static readonly Dictionary<string, char> Finals = new()
    {
        ["a"] = 'a', ["o"] = 'o', ["e"] = 'e', ["i"] = 'i', ["u"] = 'u', ["v"] = 'v',
        ["ai"] = 'd', ["ei"] = 'w', ["ao"] = 'c', ["ou"] = 'z',
        ["an"] = 'j', ["en"] = 'f', ["ang"] = 'h', ["eng"] = 'g', ["er"] = 'r',
        ["ia"] = 'x', ["ie"] = 'p', ["iao"] = 'n', ["iu"] = 'q',
        ["ian"] = 'm', ["in"] = 'b', ["iang"] = 'l', ["ing"] = 'k', ["iong"] = 's',
        ["ua"] = 'x', ["uo"] = 'o', ["uai"] = 'k', ["ui"] = 'v',
        ["uan"] = 'r', ["uang"] = 'l', ["un"] = 'y', ["ue"] = 't',
        ["ong"] = 's', ["ve"] = 't', ["vn"] = 'y',
    };

    /// <summary>带声调的拼音转成无调音节；ü 一律写成 v。</summary>
    public static string StripTone(string pinyin)
    {
        // 先把 ü 换成 v：NFD 会把 ü 拆成 u + 分音符，那时再判断已经晚了
        string prepared = pinyin
            .Replace('ü', 'v').Replace('ǖ', 'v').Replace('ǘ', 'v')
            .Replace('ǚ', 'v').Replace('ǜ', 'v');

        StringBuilder sb = new(prepared.Length);
        foreach (char raw in prepared.Normalize(NormalizationForm.FormD))
        {
            System.Globalization.UnicodeCategory category =
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(raw);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>音节 → 两键；音节不认识返回 null（宁可漏掉一个练习字，也不要瞎编码）。</summary>
    public static string? Of(string syllable)
    {
        if (syllable.Length == 0)
        {
            return null;
        }

        foreach (string prefix in new[] { "zh", "ch", "sh" })
        {
            if (syllable.StartsWith(prefix, StringComparison.Ordinal))
            {
                return TryPair(Initials[prefix], syllable[prefix.Length..]);
            }
        }

        if (syllable.Length > 1 && Initials.TryGetValue(syllable[..1], out char initial))
        {
            return TryPair(initial, syllable[1..]);
        }

        // 零声母：单字母双写（啊 aa）、双字母照全拼（爱 ai）、三字母取首字母 + 整个韵母的键（昂 ah）
        return syllable.Length switch
        {
            1 => syllable + syllable,
            2 => syllable,
            _ => Finals.TryGetValue(syllable, out char tail) ? syllable[..1] + tail : null,
        };
    }

    private static string? TryPair(char initial, string final) =>
        Finals.TryGetValue(final, out char key) ? new string([initial, key]) : null;
}
