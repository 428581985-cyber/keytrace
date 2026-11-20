using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeyTrace.Analysis;

internal readonly record struct DrillKeyStat(char Key, int Hit, int Miss);

internal sealed class DrillRecord
{
    public int Version { get; init; } = 1;
    public DateTimeOffset FinishedAt { get; init; }
    public int Total { get; init; }
    public int Correct { get; init; }
    public int Attempts { get; init; }
    public long ElapsedMs { get; init; }
    public double Accuracy { get; init; }
    public List<DrillKeyStat> Keys { get; init; } = [];
    public List<string> WrongChars { get; init; } = [];
}

/// <summary>
/// 练习页（drill.html）的成绩回流与自适应选键：
/// 把每次练完贴回来的 JSON 存到 <c>~/.keytrace/drills/</c>，结合 <c>stats.json</c>
/// 按近期加权错误率排下一轮弱键，并让已经练稳的键逐步退出。
/// </summary>
internal static class DrillHistory
{
    private const double DecayFactor = 0.75;
    private const int GraduateMinHits = 20;
    private const double GraduateMaxErrorRate = 0.03;
    private const double ActiveErrorThreshold = 0.05;

    public static bool TryParse(string json, out DrillRecord? record, out string error)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "练习结果为空。";
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "练习结果必须是 JSON 对象。";
                return false;
            }

            if (!root.TryGetProperty("v", out JsonElement verEl)
                || verEl.ValueKind != JsonValueKind.Number
                || !verEl.TryGetInt32(out int version)
                || version != 1)
            {
                error = "不支持或缺少练习结果版本号（需要 v: 1）。";
                return false;
            }

            if (!root.TryGetProperty("keys", out JsonElement keysEl) || keysEl.ValueKind != JsonValueKind.Array)
            {
                error = "缺少 keys 数组。";
                return false;
            }

            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("finishedAt", out JsonElement finEl))
            {
                if (finEl.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(
                        finEl.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out finishedAt))
                {
                    error = "finishedAt 时间格式无效。";
                    return false;
                }
            }

            Dictionary<char, (int Hit, int Miss)> mergedKeys = [];
            foreach (JsonElement item in keysEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "keys 数组元素必须是对象。";
                    return false;
                }

                string? rawKey = item.TryGetProperty("key", out JsonElement kEl) && kEl.ValueKind == JsonValueKind.String
                    ? kEl.GetString()?.Trim()
                    : null;
                if (string.IsNullOrEmpty(rawKey) || rawKey.Length != 1)
                {
                    error = "keys[].key 必须是单个英文字母。";
                    return false;
                }

                char key = char.ToLowerInvariant(rawKey[0]);
                if (key is < 'a' or > 'z')
                {
                    error = $"keys[].key 只支持 a-z，收到 '{rawKey}'。";
                    return false;
                }

                if (!TryReadNonNegativeInt(item, "hit", out int hit)
                    || !TryReadNonNegativeInt(item, "miss", out int miss))
                {
                    error = $"键 '{key}' 缺少非负整数 hit/miss。";
                    return false;
                }

                if (mergedKeys.TryGetValue(key, out (int Hit, int Miss) prev))
                {
                    mergedKeys[key] = (prev.Hit + hit, prev.Miss + miss);
                }
                else
                {
                    mergedKeys[key] = (hit, miss);
                }
            }

            List<string> wrongChars = [];
            if (root.TryGetProperty("wrongChars", out JsonElement wcEl) && wcEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement w in wcEl.EnumerateArray())
                {
                    if (w.ValueKind == JsonValueKind.String && w.GetString() is string s && s.Length > 0)
                    {
                        wrongChars.Add(s);
                    }
                }
            }

            record = new DrillRecord
            {
                Version = version,
                FinishedAt = finishedAt,
                Total = OptionalNonNegativeInt(root, "total"),
                Correct = OptionalNonNegativeInt(root, "correct"),
                Attempts = OptionalNonNegativeInt(root, "attempts"),
                ElapsedMs = OptionalNonNegativeLong(root, "elapsedMs"),
                Accuracy = OptionalDouble(root, "accuracy"),
                Keys = [.. mergedKeys.OrderBy(kv => kv.Key).Select(kv => new DrillKeyStat(kv.Key, kv.Value.Hit, kv.Value.Miss))],
                WrongChars = wrongChars,
            };
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"不是合法 JSON：{ex.Message}";
            return false;
        }
    }

    public static (string Path, DrillRecord Record) Save(string drillsDir, string rawJson)
    {
        if (!TryParse(rawJson, out DrillRecord? record, out string error) || record is null)
        {
            throw new ArgumentException(error, nameof(rawJson));
        }

        Directory.CreateDirectory(drillsDir);
        string stamp = record.FinishedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string shortId = Guid.NewGuid().ToString("N")[..6];
        string path = System.IO.Path.Combine(drillsDir, $"{stamp}-{shortId}.json");

        File.WriteAllText(path, Serialize(record), new UTF8Encoding(false));
        return (path, record);
    }

    public static List<DrillRecord> LoadRecent(string drillsDir, int maxSessions = 10)
    {
        if (maxSessions <= 0 || !Directory.Exists(drillsDir))
        {
            return [];
        }

        List<(string Name, DrillRecord Record)> loaded = [];
        foreach (string path in Directory.EnumerateFiles(drillsDir, "*.json"))
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }

            if (TryParse(text, out DrillRecord? rec, out _) && rec is not null)
            {
                loaded.Add((System.IO.Path.GetFileName(path), rec));
            }
        }

        return [.. loaded
            .OrderByDescending(x => x.Record.FinishedAt)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Take(maxSessions)
            .Select(x => x.Record)];
    }

    /// <summary>
    /// 按近期练习结果（指数衰减权重）计算推荐弱键；
    /// 累计命中达标且最近一次零失误的键从推荐和兜底列表里毕业退出。
    /// </summary>
    public static List<char> RecommendWeakKeys(
        IReadOnlyList<DrillRecord> recent,
        IReadOnlyList<char> statsWeakKeys,
        int limit = 5)
    {
        if (limit <= 0)
        {
            return [];
        }

        List<DrillRecord> ordered = [.. recent.OrderByDescending(r => r.FinishedAt)];
        Dictionary<char, KeyRollup> rollups = [];

        for (int i = 0; i < ordered.Count; i++)
        {
            double weight = Math.Pow(DecayFactor, i);
            Dictionary<char, (int Hit, int Miss)> sessionKeys = [];
            foreach (DrillKeyStat k in ordered[i].Keys)
            {
                char ch = char.ToLowerInvariant(k.Key);
                if (ch is < 'a' or > 'z' || (k.Hit == 0 && k.Miss == 0))
                {
                    continue;
                }

                if (sessionKeys.TryGetValue(ch, out (int Hit, int Miss) cur))
                {
                    sessionKeys[ch] = (cur.Hit + k.Hit, cur.Miss + k.Miss);
                }
                else
                {
                    sessionKeys[ch] = (k.Hit, k.Miss);
                }
            }

            foreach ((char ch, (int hit, int miss)) in sessionKeys)
            {
                if (!rollups.TryGetValue(ch, out KeyRollup? r))
                {
                    r = new KeyRollup();
                    rollups[ch] = r;
                }

                r.WeightedHit += hit * weight;
                r.WeightedMiss += miss * weight;
                r.RawHit += hit;
                if (!r.HasLatest)
                {
                    r.LatestMiss = miss;
                    r.HasLatest = true;
                }
            }
        }

        HashSet<char> graduated = [];
        List<(char Key, double Score)> candidates = [];

        foreach ((char ch, KeyRollup r) in rollups)
        {
            double total = r.WeightedHit + r.WeightedMiss;
            double missRate = total > 0 ? r.WeightedMiss / total : 0;

            if (r.RawHit >= GraduateMinHits && missRate <= GraduateMaxErrorRate && r.LatestMiss == 0)
            {
                graduated.Add(ch);
                continue;
            }

            if (r.WeightedMiss > 0 || missRate > ActiveErrorThreshold)
            {
                double score = missRate * (1.0 + Math.Log(1.0 + r.WeightedMiss));
                candidates.Add((ch, score));
            }
        }

        candidates.Sort((a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
        });

        List<char> result = [];
        HashSet<char> seen = [];
        foreach ((char key, _) in candidates)
        {
            if (result.Count >= limit)
            {
                break;
            }

            if (seen.Add(key))
            {
                result.Add(key);
            }
        }

        foreach (char raw in statsWeakKeys)
        {
            if (result.Count >= limit)
            {
                break;
            }

            char ch = char.ToLowerInvariant(raw);
            if (ch is >= 'a' and <= 'z' && !graduated.Contains(ch) && seen.Add(ch))
            {
                result.Add(ch);
            }
        }

        return result;
    }

    private static string Serialize(DrillRecord r)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", r.Version);
            w.WriteString("finishedAt", r.FinishedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteNumber("total", r.Total);
            w.WriteNumber("correct", r.Correct);
            w.WriteNumber("attempts", r.Attempts);
            w.WriteNumber("elapsedMs", r.ElapsedMs);
            w.WriteNumber("accuracy", Math.Round(r.Accuracy, 1));
            w.WriteStartArray("keys");
            foreach (DrillKeyStat k in r.Keys)
            {
                w.WriteStartObject();
                w.WriteString("key", k.Key.ToString());
                w.WriteNumber("hit", k.Hit);
                w.WriteNumber("miss", k.Miss);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("wrongChars");
            foreach (string c in r.WrongChars)
            {
                w.WriteStringValue(c);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryReadNonNegativeInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value)
            && value >= 0;
    }

    private static int OptionalNonNegativeInt(JsonElement parent, string name) =>
        TryReadNonNegativeInt(parent, name, out int v) ? v : 0;

    private static long OptionalNonNegativeLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement el)
        && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt64(out long v)
        && v >= 0
            ? v
            : 0;

    private static double OptionalDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement el)
        && el.ValueKind == JsonValueKind.Number
        && el.TryGetDouble(out double v)
        && !double.IsNaN(v)
        && !double.IsInfinity(v)
            ? v
            : 0;

    private sealed class KeyRollup
    {
        public double WeightedHit { get; set; }
        public double WeightedMiss { get; set; }
        public int RawHit { get; set; }
        public int LatestMiss { get; set; }
        public bool HasLatest { get; set; }
    }
}
