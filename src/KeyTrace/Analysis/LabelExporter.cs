using System.Globalization;
using System.IO;
using System.Text;

namespace KeyTrace.Analysis;

internal sealed class LabelRow
{
    public int Id { get; init; }

    /// <summary>机器判定：这一下退格算不算打错。</summary>
    public bool Predicted { get; init; }

    public string Kind { get; init; } = "";
    public long Ts { get; init; }
    public string Exe { get; init; } = "";
    public string Word { get; init; } = "";
    public string WordAfter { get; init; } = "";
    public string Deleted { get; init; } = "";
    public string Key { get; init; } = "";
    public string Replacement { get; init; } = "";
    public long? Iki { get; init; }
    public int RunLen { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// 导出金标标注表。
///
/// 为什么不是"把判错的 91 条导出来打勾"：那样只能算 precision，算不了 recall——
/// 判错的漏掉的那些永远看不到。所以判错的全部导出，再从"没判错"的池子里等比例抽样，
/// 抽样率记下来，回头用加权估 recall。
///
/// 为什么用 CSV 而不是网页或 Excel：你说过不会开终端跑服务，但 Excel 一定会开。
/// 带 BOM 的 UTF-8，双击直接看中文，填完一列保存就完事。
/// </summary>
internal static class LabelExporter
{
    private static readonly string[] Header =
    [
        "编号", "时间", "应用", "删除前的词", "删掉的字符", "按键",
        "换成了", "间隔ms", "连击", "你的判断", "备注",
    ];

    /// <summary>机器判定单独存一份，标注时不给他看——看着答案打勾，precision 必然虚高。</summary>
    private static readonly string[] KeyHeader = ["编号", "机器判定", "类型", "置信度"];

    /// <summary>判错的那几类。其余都是"没算作打错"，用来估漏判。</summary>
    private static bool IsError(string kind) => kind is "typo" or "pinyin" or "selection";

    /// <summary>
    /// 盲区（读不到文字的应用）不进抽样池：那种退格连人都判断不了"打没打错"，
    /// 抽出来只是浪费标注时间。盲区数量单独报，不当成漏判也不算成正确。
    /// </summary>
    private static bool IsSamplable(string kind) => !IsError(kind) && kind != "blind";

    public static List<LabelRow> Build(AttributionResult result, int total, int seed, bool anonymize)
    {
        List<TypoRecord> errors = [.. result.Records.Where(r => IsError(r.Kind))];
        List<TypoRecord> pool = [.. result.Records.Where(r => IsSamplable(r.Kind))];

        int need = Math.Max(0, total - errors.Count);
        Shuffle(pool, seed);
        List<TypoRecord> sampled = [.. pool.Take(Math.Min(need, pool.Count))];

        List<TypoRecord> picked = [.. errors, .. sampled];
        picked.Sort((a, b) => a.Ts.CompareTo(b.Ts));

        List<LabelRow> rows = [];
        for (int i = 0; i < picked.Count; i++)
        {
            TypoRecord r = picked[i];
            rows.Add(new LabelRow
            {
                Id = i + 1,
                Predicted = IsError(r.Kind),
                Kind = r.Kind,
                Ts = anonymize ? 0 : r.Ts,
                Exe = anonymize ? "" : r.Exe,
                Word = anonymize ? "" : Visible(r.Word),
                WordAfter = anonymize ? "" : Visible(r.WordAfter),
                Deleted = Visible(r.Deleted?.ToString() ?? ""),
                Key = r.Key,
                Replacement = Visible(r.Replacement?.ToString() ?? ""),
                Iki = r.Iki,
                RunLen = r.RunLen,
                Confidence = r.Confidence,
            });
        }

        return rows;
    }

    /// <summary>抽样率 = 抽了多少 / 池子多大。估 recall 时要用它把漏掉的还原成总量。</summary>
    public static double SampleRate(AttributionResult result, int total)
    {
        int errors = result.Records.Count(r => IsError(r.Kind));
        int pool = result.Records.Count(r => IsSamplable(r.Kind));
        int need = Math.Max(0, total - errors);
        return pool == 0 ? 0 : Math.Min(need, pool) / (double)pool;
    }

    public static string ToCsv(List<LabelRow> rows)
    {
        StringBuilder sb = new(rows.Count * 160);
        sb.Append(string.Join(',', Header)).Append("\r\n");

        foreach (LabelRow r in rows)
        {
            sb.Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Ts == 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).ToLocalTime()
                .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Cell(r.Exe)).Append(',');
            sb.Append(Cell(r.Word)).Append(',');
            sb.Append(Cell(r.Deleted)).Append(',');
            sb.Append(Cell(r.Key)).Append(',');
            sb.Append(Cell(r.Replacement)).Append(',');
            sb.Append(r.Iki?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append(r.RunLen.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(',');  // 你的判断，留空
            sb.Append("");   // 备注
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>机器判定与置信度，只在算 precision/recall 时按编号回连。</summary>
    public static string ToKeyCsv(List<LabelRow> rows)
    {
        StringBuilder sb = new(rows.Count * 48);
        sb.Append(string.Join(',', KeyHeader)).Append("\r\n");

        foreach (LabelRow r in rows)
        {
            sb.Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Predicted ? "是" : "否").Append(',');
            sb.Append(Cell(r.Kind)).Append(',');
            sb.Append(r.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>标注表落盘。key 文件放在同目录、同名加 .key，回头按编号回连。</summary>
    public static string Write(string path, List<LabelRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // 必须带 BOM：Excel 开不带 BOM 的 UTF-8 会把中文显示成乱码
        using (StreamWriter file = new(path, append: false, new UTF8Encoding(true)))
        {
            file.Write(ToCsv(rows));
        }

        string keyPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            Path.GetFileNameWithoutExtension(path) + ".key.csv");

        using (StreamWriter key = new(keyPath, append: false, new UTF8Encoding(true)))
        {
            key.Write(ToKeyCsv(rows));
        }

        return keyPath;
    }

    /// <summary>空格、换行这类字符在表格里看不见，换成能看见的记号。</summary>
    internal static string Visible(string text) => text
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace(" ", "·", StringComparison.Ordinal);

    private static string Cell(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        bool needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>固定种子洗牌：同一天同一份数据，导两次得到的表要一模一样。</summary>
    private static void Shuffle(List<TypoRecord> items, int seed)
    {
        Random random = new(seed);
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
