using System.Globalization;
using System.IO;
using System.Text;

namespace KeyTrace.Analysis;

/// <summary>
/// 导出逐键统计表（CSV），便于直接放进 Excel 或 pandas 做切片分析。
/// 与 <c>docs/export-formats.md</c> 的扩展口约定一致：一种格式一个文件，不改动上游归因逻辑。
/// </summary>
internal static class CsvExport
{
    private const string Header =
        "vk,name,role,presses,shifted,typos,typosUnreplaced,pinyinTypos,"
        + "typoRate,ikiMeanMs,ikiP50Ms,ikiP90Ms,dwellMeanMs,ikiRatio,weakScore";

    public static string Build(AttributionResult result, int minKeySamples = 20)
    {
        StringBuilder sb = new(2048);
        sb.Append(Header).Append("\r\n");

        foreach (KeyStat k in result.Keys.Values.OrderBy(x => x.Vk))
        {
            List<long> iki = [.. k.Iki];
            iki.Sort();

            double p50 = Attributor.Percentile(iki, 0.5);
            double p90 = Attributor.Percentile(iki, 0.9);
            double ikiMean = iki.Count == 0 ? 0 : iki.Average();
            double dwellMean = k.Dwell.Count == 0 ? 0 : k.Dwell.Average();
            double ratio = result.Baseline.GlobalP50 > 0 ? p50 / result.Baseline.GlobalP50 : 0;
            double typoRate = k.Presses > 0 ? (double)k.Typos / k.Presses : 0;
            double? weakScore = StatsBuilder.WeakScore(k, ratio, minKeySamples);

            sb.Append(k.Vk.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Cell(KeyCatalog.Name(k.Vk))).Append(',');
            sb.Append(Cell(StatsBuilder.RoleName(KeyCatalog.Role(k.Vk)))).Append(',');
            sb.Append(k.Presses.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(k.Shifted.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(k.Typos.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(k.TyposUnreplaced.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(k.PinyinTypos.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Fmt(typoRate, 4)).Append(',');
            sb.Append(Fmt(ikiMean, 1)).Append(',');
            sb.Append(Fmt(p50, 1)).Append(',');
            sb.Append(Fmt(p90, 1)).Append(',');
            sb.Append(Fmt(dwellMean, 1)).Append(',');
            sb.Append(Fmt(ratio, 2)).Append(',');
            sb.Append(weakScore is double score ? Fmt(score, 3) : "");
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    public static void Write(string path, string csv)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // 带 BOM 的 UTF-8：Windows 下双击用 Excel 打开不会把列名/非 ASCII 字符显示乱
        File.WriteAllText(path, csv, new UTF8Encoding(true));
    }

    private static string Fmt(double value, int digits)
    {
        double clean = double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Round(value, digits);
        return clean.ToString(CultureInfo.InvariantCulture);
    }

    private static string Cell(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        bool needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
