using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeyTrace.Analysis;

internal sealed class StatsContext
{
    public string Directory { get; init; } = "";
    public List<string> Files { get; init; } = [];
    public int Events { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public double PauseFactor { get; init; }
    public int TypoMaxBackspaces { get; init; }
    public int MinKeySamples { get; init; }
}

/// <summary>
/// 把归因结果落成 <c>stats.json</c> 和 <c>typos.jsonl</c>。
///
/// 手写顺序而不是反射序列化：字段顺序固定后，两次运行的 diff 才有意义，
/// 而且 stats.json 是要给人看的（还会被看板直接读）。
/// </summary>
internal static class StatsBuilder
{
    private const double TypoRateCeiling = 0.15;
    private const double SlowRatioCeiling = 2.5;

    private static readonly JsonSerializerOptions Quoting = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteJson(string path, AttributionResult r, StatsContext ctx)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using FileStream file = File.Create(path);
        using Utf8JsonWriter w = new(file, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        w.WriteStartObject();
        w.WriteNumber("v", 1);
        w.WriteString("generatedAt", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        w.WriteStartObject("window");
        w.WriteString("from", ctx.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.WriteString("to", ctx.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.WriteNumber("days", ctx.To.DayNumber - ctx.From.DayNumber + 1);
        w.WriteStartArray("files");
        foreach (string f in ctx.Files)
        {
            w.WriteStringValue(Path.GetFileName(f));
        }

        w.WriteEndArray();
        w.WriteNumber("events", ctx.Events);
        w.WriteStartObject("skipped");
        w.WriteNumber("paste", r.Skipped.Paste);
        w.WriteNumber("shortcut", r.Skipped.Shortcut);
        w.WriteNumber("hotkey", r.Skipped.Hotkey);
        w.WriteNumber("glyph", r.Skipped.Glyph);
        w.WriteNumber("synthetic", r.Skipped.Synthetic);
        w.WriteNumber("emptyBackspace", r.Skipped.EmptyBackspace);
        w.WriteNumber("deleteKey", r.Skipped.DeleteKey);
        w.WriteNumber("unparsable", r.Skipped.Unparsable);
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("config");
        w.WriteString("ime", r.Meta.TryGetValue("ime", out string? ime) ? ime : "unknown");
        w.WriteNumber("typoMaxBackspaces", ctx.TypoMaxBackspaces);
        w.WriteString("pauseMode", "median-times-factor");
        w.WriteNumber("pauseFactor", ctx.PauseFactor);
        w.WriteNumber("minKeySamples", ctx.MinKeySamples);
        w.WriteEndObject();

        WriteBaseline(w, r);
        WriteTotals(w, r);
        WriteKeys(w, r, ctx);
        WriteSymbols(w, r);
        WriteErrors(w, r);
        WriteChinese(w, r);
        WriteApps(w, r);

        w.WriteEndObject();
        w.Flush();
    }

    private static void WriteBaseline(Utf8JsonWriter w, AttributionResult r)
    {
        Baseline b = r.Baseline;
        w.WriteStartObject("baseline");
        w.WriteNumber("presses", b.Presses);
        w.WriteNumber("ikiMeanMs", Round(b.GlobalMean, 1));
        w.WriteNumber("ikiP50Ms", Round(b.GlobalP50, 1));
        w.WriteNumber("ikiSdMs", Round(b.GlobalSd, 1));
        w.WriteNumber("pauseThresholdMs", Round(b.GlobalP50 * b.PauseFactor, 1));
        w.WriteNumber("activeMs", r.Totals.ActiveMs);
        w.WriteNumber("cpm", Round(Cpm(r), 1));
        w.WriteNumber("typoRate", Round(Rate(r.Totals.Typos, r.Totals.Keystrokes), 4));
        w.WriteEndObject();
    }

    private static void WriteTotals(Utf8JsonWriter w, AttributionResult r)
    {
        w.WriteStartObject("totals");
        w.WriteNumber("keystrokes", r.Totals.Keystrokes);
        w.WriteNumber("chars", r.Totals.Chars);
        w.WriteNumber("commits", r.Totals.Commits);
        w.WriteNumber("sessions", r.Totals.Sessions);
        w.WriteNumber("backspaces", r.Totals.Backspaces);
        w.WriteNumber("typos", r.Totals.Typos);
        w.WriteNumber("pinyinErrors", r.Totals.PinyinErrors);
        w.WriteNumber("selectionErrors", r.Totals.SelectionErrors);
        w.WriteNumber("rewording", r.Totals.Rewording);
        w.WriteNumber("collateral", r.Totals.Collateral);
        w.WriteEndObject();
    }

    private static void WriteKeys(Utf8JsonWriter w, AttributionResult r, StatsContext ctx)
    {
        w.WriteStartArray("keys");

        foreach (KeyStat k in r.Keys.Values.OrderBy(x => x.Vk))
        {
            List<long> iki = k.Iki;
            iki.Sort();
            double p50 = Attributor.Percentile(iki, 0.5);
            double ratio = r.Baseline.GlobalP50 > 0 ? p50 / r.Baseline.GlobalP50 : 0;

            w.WriteStartObject();
            w.WriteNumber("vk", k.Vk);
            w.WriteString("name", KeyCatalog.Name(k.Vk));
            w.WriteString("role", RoleName(KeyCatalog.Role(k.Vk)));
            w.WriteNumber("presses", k.Presses);
            w.WriteNumber("shifted", k.Shifted);
            w.WriteNumber("typos", k.Typos);
            w.WriteNumber("pinyinTypos", k.PinyinTypos);
            w.WriteNumber("typoRate", Round(Rate(k.Typos, k.Presses), 4));
            w.WriteNumber("ikiMeanMs", Round(iki.Count == 0 ? 0 : iki.Average(), 1));
            w.WriteNumber("ikiP50Ms", Round(p50, 1));
            w.WriteNumber("ikiP90Ms", Round(Attributor.Percentile(iki, 0.9), 1));
            w.WriteNumber("pauseThresholdMs", Round(r.Baseline.Threshold(k.Vk), 1));
            w.WriteNumber("dwellMeanMs", Round(k.Dwell.Count == 0 ? 0 : k.Dwell.Average(), 1));
            w.WriteNumber("ikiRatio", Round(ratio, 2));

            double? score = WeakScore(k, ratio, ctx.MinKeySamples);
            if (score is null)
            {
                w.WriteNull("weakScore");
            }
            else
            {
                w.WriteNumber("weakScore", Round(score.Value, 3));
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>
    /// 弱键评分：错误率占七成，相对慢速占三成，两项都做绝对归一。
    /// 思路来自 keybr 的逐键置信度（错误优先、速度次之、样本不足不排名），
    /// 但不做指数平滑——这里是一次性离线统计，没有"下一课"要调。
    /// </summary>
    internal static double? WeakScore(KeyStat k, double ikiRatio, int minSamples)
    {
        KeyRole role = KeyCatalog.Role(k.Vk);
        if (role is not (KeyRole.Letter or KeyRole.Digit or KeyRole.Symbol))
        {
            return 0;
        }

        if (k.Presses < minSamples)
        {
            return null;
        }

        double errPart = Math.Min(1, Rate(k.Typos, k.Presses) / TypoRateCeiling);
        double slowPart = Math.Clamp((ikiRatio - 1) / (SlowRatioCeiling - 1), 0, 1);
        return 0.7 * errPart + 0.3 * slowPart;
    }

    private static void WriteSymbols(Utf8JsonWriter w, AttributionResult r)
    {
        w.WriteStartArray("symbols");
        foreach (SymbolStat s in r.Symbols.Values.OrderBy(x => x.Char))
        {
            w.WriteStartObject();
            w.WriteString("char", s.Char.ToString());
            w.WriteNumber("vk", s.Vk);
            w.WriteBoolean("shift", s.Shift);
            w.WriteNumber("count", s.Count);
            w.WriteNumber("typos", s.Typos);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteErrors(Utf8JsonWriter w, AttributionResult r)
    {
        w.WriteStartObject("errors");
        w.WriteStartObject("causes");
        foreach (IGrouping<string, TypoRecord> group in r.Records.GroupBy(x => x.Cause).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(group.Key, group.Count());
        }

        w.WriteEndObject();
        w.WriteStartObject("rewording");
        w.WriteNumber("longPause", r.Totals.RewordingLongPause);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteChinese(Utf8JsonWriter w, AttributionResult r)
    {
        ChineseCounters c = r.Chinese;
        w.WriteStartObject("chinese");
        w.WriteNumber("commits", c.Commits);
        w.WriteNumber("chars", c.Chars);
        w.WriteNumber("comps", c.Comps);
        if (c.Commits > 0)
        {
            w.WriteNumber("compCoverage", Round((double)c.CommitsWithComp / c.Commits, 2));
        }
        else
        {
            w.WriteNull("compCoverage");
        }

        w.WriteNumber("pinyinErrors", c.PinyinErrors);
        w.WriteNumber("selectionErrors", c.SelectionErrors);
        w.WriteStartArray("keys");
        foreach ((int vk, int count) in c.PinyinErrorKeys.OrderByDescending(x => x.Value))
        {
            w.WriteStartObject();
            w.WriteNumber("vk", vk);
            w.WriteString("name", KeyCatalog.Name(vk));
            w.WriteNumber("count", count);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteApps(Utf8JsonWriter w, AttributionResult r)
    {
        w.WriteStartArray("apps");
        foreach (AppStat a in r.Apps.Values.OrderByDescending(x => x.Keystrokes).ThenBy(x => x.Exe, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("exe", a.Exe);
            w.WriteNumber("keystrokes", a.Keystrokes);
            w.WriteNumber("commits", a.Commits);
            w.WriteNumber("chars", a.Chars);
            w.WriteBoolean("textAvailable", a.TextAvailable);
            w.WriteNumber("typos", a.Typos);
            w.WriteNumber("backspacePer100", Round(a.Chars > 0 ? a.Backspaces * 100.0 / a.Chars : 0, 1));
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    public static void WriteTypos(string path, AttributionResult r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using StreamWriter file = new(path, append: false, new UTF8Encoding(false));
        foreach (TypoRecord t in r.Records.OrderBy(x => x.Ts))
        {
            StringBuilder sb = new(256);
            sb.Append("{\"ts\":").Append(t.Ts.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"sid\":").Append(Q(t.Sid));
            sb.Append(",\"exe\":").Append(Q(t.Exe));
            sb.Append(",\"kind\":").Append(Q(t.Kind));
            sb.Append(",\"vk\":").Append(t.Vk.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"key\":").Append(Q(t.Key));
            sb.Append(",\"deleted\":").Append(t.Deleted is char d ? Q(d.ToString()) : "null");
            sb.Append(",\"replacement\":").Append(t.Replacement is char rp ? Q(rp.ToString()) : "null");
            sb.Append(",\"runLen\":").Append(t.RunLen.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"iki\":").Append(t.Iki is long iki ? iki.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"cause\":").Append(Q(t.Cause));
            sb.Append(",\"confidence\":").Append(t.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(",\"word\":").Append(Q(t.Word));
            sb.Append(",\"pos\":").Append(t.Position.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            file.WriteLine(sb.ToString());
        }
    }

    private static string Q(string value) => JsonSerializer.Serialize(value, Quoting);

    private static double Rate(int part, int total) => total <= 0 ? 0 : (double)part / total;

    private static double Cpm(AttributionResult r) =>
        r.Totals.ActiveMs <= 0 ? 0 : r.Totals.Chars * 60000.0 / r.Totals.ActiveMs;

    private static double Round(double value, int digits) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Round(value, digits);

    internal static string RoleName(KeyRole role) => role switch
    {
        KeyRole.Modifier => "modifier",
        KeyRole.Letter => "letter",
        KeyRole.Digit => "digit",
        KeyRole.Symbol => "symbol",
        KeyRole.Space => "space",
        KeyRole.Enter => "enter",
        KeyRole.Tab => "tab",
        KeyRole.Edit => "edit",
        KeyRole.Navigate => "navigate",
        KeyRole.Function => "function",
        _ => "other",
    };
}
