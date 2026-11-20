using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace KeyTrace.Analysis;

/// <summary>
/// 归因层读到的一行事件。与 <c>docs/event-schema.md</c> 对齐，字段"只加不改"，
/// 所以这里多留了 <see cref="Version"/>：遇到不认识的版本要拒读，不能猜。
/// </summary>
internal readonly record struct EventRecord(
    int Version,
    long Ts,
    long Mono,
    string Ev,
    int Vk,
    int Sc,
    int Mods,
    string Exe,
    string Sid,
    string? Text,
    string? Src,
    int? Keys)
{
    public const int SupportedVersion = 1;

    /// <summary>Ctrl / Alt / Win 任一按住：这是快捷键，不是打字。</summary>
    public bool HasCommandModifier => (Mods & 0b1110) != 0;

    public bool HasShift => (Mods & 0b0001) != 0;

    public static bool TryParse(string line, out EventRecord record)
    {
        record = default;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            record = new EventRecord(
                Version: Int(root, "v") ?? 0,
                Ts: Long(root, "ts"),
                Mono: Long(root, "mono"),
                Ev: Str(root, "ev") ?? string.Empty,
                Vk: Int(root, "vk") ?? 0,
                Sc: Int(root, "sc") ?? 0,
                Mods: Int(root, "mods") ?? 0,
                Exe: Str(root, "exe") ?? "unknown",
                Sid: Str(root, "sid") ?? string.Empty,
                Text: Str(root, "text"),
                Src: Str(root, "src"),
                Keys: Int(root, "keys"));

            return record.Version == SupportedVersion && record.Ev.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32()
            : null;

    private static long Long(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement e) || e.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (e.TryGetInt64(out long value))
        {
            return value;
        }

        return (long)e.GetDouble();
    }
}

/// <summary>按天找日志：<c>YYYY-MM-DD.jsonl</c> 与 <c>YYYY-MM-DD.jsonl.gz</c> 一起读。</summary>
internal static class EventLog
{
    public static (List<string> Files, IEnumerable<EventRecord> Events, int Rejected) Read(
        string directory,
        DateOnly? since,
        DateOnly today)
    {
        List<string> files = [];
        List<EventRecord> events = [];
        int rejected = 0;

        if (!Directory.Exists(directory))
        {
            return (files, events, rejected);
        }

        foreach (string path in FindFiles(directory, since, today))
        {
            files.Add(path);
            foreach (string line in ReadLines(path))
            {
                if (EventRecord.TryParse(line, out EventRecord record))
                {
                    events.Add(record);
                }
                else
                {
                    rejected++;
                }
            }
        }

        // 文件之间可能有跨天残留或多次重启，先按会话时间排再按单调时钟排，后面的状态机才敢顺序扫
        SortChronologically(events);
        return (files, events, rejected);
    }

    /// <summary>
    /// 按真实时间线排序：会话起始墙钟时间 → Sid → 会话内单调时钟 Mono → Ts。
    /// 进程重启后 Mono 会从 0 重计，直接按 Mono 排会把多轮运行的会话交错在一起；
    /// 同一会话内仍以 Mono 为准，免疫系统对时造成的 Ts 跳变。
    /// </summary>
    internal static void SortChronologically(List<EventRecord> events)
    {
        if (events.Count <= 1)
        {
            return;
        }

        Dictionary<string, long> sessionStartTs = [];
        foreach (EventRecord e in events)
        {
            if (e.Sid.Length == 0)
            {
                continue;
            }

            if (!sessionStartTs.TryGetValue(e.Sid, out long start) || e.Ts < start)
            {
                sessionStartTs[e.Sid] = e.Ts;
            }
        }

        events.Sort((a, b) =>
        {
            long aStart = a.Sid.Length > 0 ? sessionStartTs[a.Sid] : a.Ts;
            long bStart = b.Sid.Length > 0 ? sessionStartTs[b.Sid] : b.Ts;
            int cmp = aStart.CompareTo(bStart);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.CompareOrdinal(a.Sid, b.Sid);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Mono.CompareTo(b.Mono);
            return cmp != 0 ? cmp : a.Ts.CompareTo(b.Ts);
        });
    }

    private static IEnumerable<string> FindFiles(string directory, DateOnly? since, DateOnly today)
    {
        List<(DateOnly Day, string Path)> found = [];

        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            string stem = name.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(name))
                : Path.GetFileNameWithoutExtension(name);

            if (!DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
            {
                continue;
            }

            if (since is not null && day < since)
            {
                continue;
            }

            if (day > today)
            {
                continue;
            }

            found.Add((day, path));
        }

        found.Sort((a, b) => a.Day.CompareTo(b.Day));
        return found.Select(x => x.Path);
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream file = File.OpenRead(path);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using StreamReader reader = new(gzip, System.Text.Encoding.UTF8);
            return ReadAll(reader);
        }

        // 必须允许写入共享：常驻进程正按追加模式写着当天的文件，
        // 用 File.ReadLines 会因为共享模式不匹配直接抛 IOException。
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader text = new(stream, System.Text.Encoding.UTF8);
        return ReadAll(text);
    }

    private static List<string> ReadAll(StreamReader reader)
    {
        List<string> lines = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }
}
