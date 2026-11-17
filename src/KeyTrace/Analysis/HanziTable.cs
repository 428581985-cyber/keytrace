using System.IO;
using System.Reflection;

namespace KeyTrace.Analysis;

/// <summary>一个汉字：音节、词频、小鹤双拼两键。</summary>
internal readonly record struct HanziEntry(char Char, string Syllable, int Freq, string Code);

/// <summary>
/// 内置字表（7258 字），由 <c>tools/gen_zh_data.py</c> 从
/// <c>mozillazg/pinyin-data</c>（MIT）和 <c>fxsjy/jieba</c>（MIT）生成。
/// 编进程序集：单文件发布时不用管数据文件路径。
/// </summary>
internal static class HanziTable
{
    private static readonly Lazy<List<HanziEntry>> Cached = new(Load);

    public static IReadOnlyList<HanziEntry> All => Cached.Value;

    private static List<HanziEntry> Load()
    {
        Assembly assembly = typeof(HanziTable).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("zh-hanzi.tsv", StringComparison.Ordinal));

        if (name is null)
        {
            return [];
        }

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);

        List<HanziEntry> entries = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 4
                || parts[0].Length != 1
                || !int.TryParse(parts[2], out int freq))
            {
                continue;
            }

            entries.Add(new HanziEntry(parts[0][0], parts[1], freq, parts[3]));
        }

        return entries;
    }
}

/// <summary>
/// 按弱键选练习字。
///
/// 中文和英文不一样：英文想练某个键，可以从含该字母的单词里挑；中文得先知道
/// 每个字的双拼码，再看码里有没有那个键。「好」= h + c，所以 c 弱就练「好」。
/// 这就是不能用 keybr 那套音素生成器的原因——它生成的是伪词，中文没有对应物。
/// </summary>
internal static class ZhSelector
{
    /// <summary>每批混入多少"已掌握键"的字，避免练成只会打难字的偏科（keybr 的做法）。</summary>
    internal const double KnownMix = 0.2;

    public static List<HanziEntry> Pick(IReadOnlySet<char> weakKeys, int count)
    {
        if (count <= 0 || weakKeys.Count == 0 || HanziTable.All.Count == 0)
        {
            return [];
        }

        List<(HanziEntry Entry, int Hits)> hits = [];
        List<HanziEntry> mastered = [];

        foreach (HanziEntry entry in HanziTable.All)
        {
            int hit = entry.Code.Count(weakKeys.Contains);
            if (hit > 0)
            {
                hits.Add((entry, hit));
            }
            else
            {
                mastered.Add(entry);
            }
        }

        // 命中越多越前；同样命中数按词频排，常用字先练
        hits.Sort((a, b) => a.Hits != b.Hits ? b.Hits.CompareTo(a.Hits) : b.Entry.Freq.CompareTo(a.Entry.Freq));

        int filler = (int)Math.Round(count * KnownMix);
        int wanted = count - filler;

        List<HanziEntry> focused = [.. hits.Take(wanted).Select(h => h.Entry)];

        // 已掌握的字同样按词频取，保证混入的也是常用字，不是生僻字凑数
        List<HanziEntry> fillers = [];
        foreach (HanziEntry entry in mastered.OrderByDescending(e => e.Freq))
        {
            if (fillers.Count >= filler)
            {
                break;
            }

            fillers.Add(entry);
        }

        // 均匀插进去，而不是全堆在末尾——堆在末尾等于"先练难字再练简单字"，
        // 起不到缓解偏科的作用。
        List<HanziEntry> picked = [];
        int next = 0;
        int step = fillers.Count > 0 ? Math.Max(1, focused.Count / fillers.Count) : int.MaxValue;
        for (int i = 0; i < focused.Count; i++)
        {
            picked.Add(focused[i]);
            if (fillers.Count > 0 && (i + 1) % step == 0 && next < fillers.Count)
            {
                picked.Add(fillers[next++]);
            }
        }

        while (next < fillers.Count)
        {
            picked.Add(fillers[next++]);
        }

        return picked;
    }

    /// <summary>命中统计，用来解释"为什么这几个字会被选中"。</summary>
    public static Dictionary<char, int> Coverage(IEnumerable<HanziEntry> picked)
    {
        Dictionary<char, int> counts = [];
        foreach (HanziEntry entry in picked)
        {
            foreach (char key in entry.Code)
            {
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        return counts;
    }
}
