using System.Text.Json;

namespace KeyTrace.Analysis;

/// <summary>
/// 按弱符号选代码风格练习串（供 <c>keytrace words --lang sym</c> 使用）。
/// 规则与 <see cref="ZhSelector"/> 对齐：命中弱符号越多、越能补齐未覆盖符号的串排前面，
/// 同时均匀混入约 20% 不含弱符号的常用串，避免练成只盯几个键的偏科。
/// </summary>
internal static class SymbolSelector
{
    /// <summary>每批混入多少"已掌握符号"的短串。</summary>
    internal const double KnownMix = 0.2;

    internal static readonly char[] DefaultWeakSymbols = ['{', '}', '[', ']', '(', ')', ';', '='];

    private static readonly string[] Corpus =
    [
        "{}", "[]", "()", "a[i]", "fn(x)", "{k:v}", "x=1;", "a==b",
        "a!=b", "x+=1", "i-=1", "a->b", "x=>y", "std::io", "a&&b", "a||b",
        "\"ok\"", "'c'", "a_b", "i<n", "n>=0", "a/b", "a*b", "100%",
        "#inc", "@id", "$var", "~/app", "a^b", "x?a:b", "path\\to", "!ok",
        "~0", "`id`", "...", "a,b", "fn(a,b)", "k[0];", "{a,b}", "m[\"k\"]",
        "v[i]=", "{x:0};", "([a])", "[a,b];", "a<=b", "a>0", "x*=2", "x/=2",
        "err??0", "fn();", "map<K,V>", "arr[0..2]", "${id}", "#[ok]", "|x|x", "a&b",
        "s=\"\";", "c='\\n';", "id_1;", "v?.len",
    ];

    public static List<string> Pick(IReadOnlySet<char> weakSymbols, int count)
    {
        if (count <= 0 || weakSymbols.Count == 0)
        {
            return [];
        }

        List<string> focused = [];
        List<string> mastered = [];

        foreach (string token in Corpus)
        {
            if (token.Any(weakSymbols.Contains))
            {
                focused.Add(token);
            }
            else
            {
                mastered.Add(token);
            }
        }

        if (focused.Count == 0)
        {
            return [];
        }

        int filler = mastered.Count == 0 ? 0 : (int)Math.Round(count * KnownMix);
        int wanted = count - filler;

        List<string> selected = SelectFocused(focused, weakSymbols, wanted);
        List<string> fillers = [];
        for (int i = 0; i < filler; i++)
        {
            fillers.Add(mastered[i % mastered.Count]);
        }

        return Interleave(selected, fillers);
    }

    public static Dictionary<char, int> Coverage(IEnumerable<string> picked)
    {
        Dictionary<char, int> counts = [];
        foreach (string token in picked)
        {
            foreach (char c in token)
            {
                if (IsSymbol(c))
                {
                    counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
                }
            }
        }

        return counts;
    }

    public static HashSet<char> ParseWeakSpec(string spec)
    {
        string trimmed = spec.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (!trimmed.Contains(','))
        {
            return [.. trimmed.Where(c => !char.IsWhiteSpace(c))];
        }

        if (trimmed == ",")
        {
            return [','];
        }

        // 若每个逗号都干净地夹在非空分段之间，把它当作分隔符（如 "{},[]"、"{,},;"）；
        // 否则（如 ",.;"、",,"、";,"）说明用户想练逗号本身。
        string[] parts = trimmed.Split(',');
        if (parts.All(p => p.Trim().Length > 0))
        {
            return [.. parts.SelectMany(p => p).Where(c => !char.IsWhiteSpace(c))];
        }

        return [.. trimmed.Where(c => !char.IsWhiteSpace(c))];
    }

    public static List<char> WeakSymbolsFromStatsJson(string statsJson, int limit = 6)
    {
        if (limit <= 0)
        {
            return [];
        }

        List<char> picked = [];
        HashSet<char> seen = [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(statsJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("symbols", out JsonElement symArr) && symArr.ValueKind == JsonValueKind.Array)
            {
                List<(char Ch, int Typos, int Count)> rows = [];
                foreach (JsonElement el in symArr.EnumerateArray())
                {
                    string? text = el.TryGetProperty("char", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                        ? cEl.GetString()
                        : null;
                    int typos = el.TryGetProperty("typos", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.Number
                        ? tEl.GetInt32()
                        : 0;
                    int count = el.TryGetProperty("count", out JsonElement cntEl) && cntEl.ValueKind == JsonValueKind.Number
                        ? cntEl.GetInt32()
                        : 0;

                    if (text is { Length: 1 } && typos > 0 && IsSymbol(text[0]))
                    {
                        rows.Add((text[0], typos, count));
                    }
                }

                foreach ((char ch, _, _) in rows.OrderByDescending(r => r.Typos).ThenByDescending(r => r.Count))
                {
                    if (picked.Count >= limit)
                    {
                        return picked;
                    }

                    if (seen.Add(ch))
                    {
                        picked.Add(ch);
                    }
                }
            }

            if (root.TryGetProperty("keys", out JsonElement keyArr) && keyArr.ValueKind == JsonValueKind.Array)
            {
                List<(int Vk, double Score)> symKeys = [];
                foreach (JsonElement el in keyArr.EnumerateArray())
                {
                    string? role = el.TryGetProperty("role", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString()
                        : null;
                    if (role != "symbol")
                    {
                        continue;
                    }

                    if (!el.TryGetProperty("weakScore", out JsonElement sEl) || sEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    double score = sEl.GetDouble();
                    if (score <= 0)
                    {
                        continue;
                    }

                    int vk = el.TryGetProperty("vk", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.Number
                        ? vEl.GetInt32()
                        : 0;
                    if (vk > 0)
                    {
                        symKeys.Add((vk, score));
                    }
                }

                foreach ((int vk, _) in symKeys.OrderByDescending(k => k.Score))
                {
                    foreach (bool shift in new[] { false, true })
                    {
                        if (picked.Count >= limit)
                        {
                            return picked;
                        }

                        if (KeyCatalog.Char(vk, shift) is char ch && IsSymbol(ch) && seen.Add(ch))
                        {
                            picked.Add(ch);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [.. DefaultWeakSymbols.Take(limit)];
        }

        return picked.Count > 0 ? picked : [.. DefaultWeakSymbols.Take(limit)];
    }

    private static List<string> SelectFocused(List<string> focused, IReadOnlySet<char> weakSymbols, int wanted)
    {
        List<string> selected = new(wanted);
        HashSet<char> covered = [];
        int[] usedCount = new int[focused.Count];

        for (int step = 0; step < wanted; step++)
        {
            int bestIdx = 0;
            (int Uncovered, int NegUsed, int Distinct, int Total, int NegIdx) bestRank =
                (int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue);

            for (int i = 0; i < focused.Count; i++)
            {
                string token = focused[i];
                int uncovered = 0;
                int distinct = 0;
                foreach (char w in weakSymbols)
                {
                    if (token.Contains(w))
                    {
                        distinct++;
                        if (!covered.Contains(w))
                        {
                            uncovered++;
                        }
                    }
                }

                int total = token.Count(weakSymbols.Contains);
                var rank = (uncovered, -usedCount[i], distinct, total, -i);
                if (rank.CompareTo(bestRank) > 0)
                {
                    bestRank = rank;
                    bestIdx = i;
                }
            }

            string chosen = focused[bestIdx];
            usedCount[bestIdx]++;
            foreach (char w in weakSymbols)
            {
                if (chosen.Contains(w))
                {
                    covered.Add(w);
                }
            }

            selected.Add(chosen);
        }

        return selected;
    }

    private static List<string> Interleave(List<string> focused, List<string> fillers)
    {
        List<string> picked = new(focused.Count + fillers.Count);
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

    private static bool IsSymbol(char c) => c < 128 && !char.IsWhiteSpace(c) && !char.IsLetterOrDigit(c);
}
