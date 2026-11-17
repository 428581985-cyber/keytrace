using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeyTrace.Analysis;

/// <summary>
/// 导出 keybr-tui 能导入的 <c>typing-data.json</c>。
///
/// 为什么不 fork keybr 改代码：keybr.com 是 AGPL-3.0，网络 copyleft，搬进来整个项目都得跟着
/// 换许可；而它内部需要的每键统计跟我们已经算出来的东西几乎一一对应。所以只对接格式，
/// 让它的算法在我们的真实数据上跑——我们不改它一行代码，也不碰许可。
///
/// 只导出英文字母和数字：keybr 是英文打字器，而且我们把 <c>[</c> 和 <c>{</c> 算在同一个物理键上，
/// 分不出 codePoint，硬塞进去只会让它的模型学到错的分布。符号和中文的练习材料由阶段 4 自己出。
/// </summary>
internal static class KeybrExport
{
    private sealed class Session
    {
        public DateOnly Day { get; init; }
        public AttributionResult Result { get; init; } = new();
    }

    public static string Build(IReadOnlyList<(DateOnly Day, AttributionResult Result)> days)
    {
        List<Session> sessions = [.. days.Select(d => new Session { Day = d.Day, Result = d.Result })];

        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartArray();

            foreach (Session session in sessions)
            {
                List<(int CodePoint, int Hits, int Misses, int TimeToType)> keys = KeysOf(session.Result);
                if (keys.Count == 0)
                {
                    continue;
                }

                int length = keys.Sum(k => k.Hits);
                long time = session.Result.Totals.TextActiveMs;
                if (length == 0 || time <= 0)
                {
                    continue;
                }

                w.WriteStartObject();
                w.WriteString("layout", "en-us");
                w.WriteString("textType", "generated");
                w.WriteString("timeStamp", session.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)
                    .ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                w.WriteNumber("length", length);
                w.WriteNumber("time", time);
                w.WriteNumber("errors", keys.Sum(k => k.Misses));
                w.WriteNumber("speed", Math.Round(length * 60000.0 / time, 2));

                w.WriteStartArray("histogram");
                foreach ((int codePoint, int hits, int misses, int timeToType) in keys)
                {
                    w.WriteStartObject();
                    w.WriteNumber("codePoint", codePoint);
                    w.WriteNumber("hitCount", hits);
                    w.WriteNumber("missCount", misses);
                    w.WriteNumber("timeToType", timeToType);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    /// <summary>把一天的逐键统计折成 keybr 的 histogram。只留 a–z 和 0–9。</summary>
    internal static List<(int CodePoint, int Hits, int Misses, int TimeToType)> KeysOf(AttributionResult result)
    {
        List<(int, int, int, int)> rows = [];

        foreach (KeyStat key in result.Keys.Values.OrderBy(k => k.Vk))
        {
            int? codePoint = CodePointOf(key.Vk);
            if (codePoint is null || key.Presses == 0)
            {
                continue;
            }

            List<long> iki = key.Iki;
            iki.Sort();
            int timeToType = (int)Math.Round(Attributor.Percentile(iki, 0.5));

            // missCount 只算确认的纠错：删完原位补了别的东西。
            // 删了不补的那类连人都判断不了，塞进 keybr 只会让它误以为这个键很烂。
            rows.Add((codePoint.Value, key.Presses, key.Typos, timeToType));
        }

        return rows;
    }

    private static int? CodePointOf(int vk)
    {
        if (vk is >= 65 and <= 90)
        {
            return 97 + (vk - 65); // 小写字母
        }

        if (vk is >= 48 and <= 57)
        {
            return vk;
        }

        return null;
    }
}
