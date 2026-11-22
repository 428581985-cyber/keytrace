using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KeyTrace.Analysis;
using KeyTrace.Collect;

namespace KeyTrace.Ui;

internal enum StatsRange
{
    Today,
    Days7,
    All,
}

internal sealed record DailyPoint(DateOnly Day, int Keystrokes, int Typos, double AccuracyPct);

internal sealed record HourlyPoint(int Hour, int Keystrokes, int Typos);

internal sealed record KeyCellMetric(
    int Vk,
    string Name,
    KeyRole Role,
    int Presses,
    int Typos,
    int TyposUnreplaced,
    double TypoRate,
    double MedianIkiMs,
    double Wpm,
    double? WeakScore);

internal sealed record WeakSymbolMetric(char Char, int Vk, bool Shift, int Presses, int Typos, double TypoRate);

internal sealed record DesktopAppMetric(string Exe, int Keystrokes, int Commits, int Typos, int Backspaces, bool TextAvailable, double AccuracyPct);

internal sealed record DesktopCommitItem(long Ts, string Exe, string Text, int Keys);

internal sealed class DesktopSnapshot
{
    public StatsRange Range { get; init; }
    public DateTime GeneratedAt { get; init; }
    public string ImeName { get; init; } = "unknown";
    public long TodayLogBytes { get; init; }
    public int DaysCount { get; init; }
    public int TotalEvents { get; init; }
    public int FilesCount { get; init; }
    public long TodayKeystrokesFromDisk { get; init; }
    public long MaxTodayEventTs { get; init; }

    public int Keystrokes { get; init; }
    public long ActiveMs { get; init; }
    public long TextActiveMs { get; init; }
    public double Wpm { get; init; }
    public double AccuracyPct { get; init; }
    public int Typos { get; init; }
    public int Commits { get; init; }
    public int Chars { get; init; }
    public int PinyinErrors { get; init; }
    public int SelectionErrors { get; init; }
    public AccuracyReport AccuracyReport { get; init; } = new(100, AccuracyGuard.DefaultThresholdPercent, false, []);

    public IReadOnlyList<DailyPoint> DailySeries { get; init; } = [];
    public IReadOnlyList<HourlyPoint> HourlySeries { get; init; } = [];
    public IReadOnlyDictionary<int, KeyCellMetric> KeysByVk { get; init; } = new Dictionary<int, KeyCellMetric>();
    public IReadOnlyList<KeyCellMetric> WeakLetters { get; init; } = [];
    public IReadOnlyList<WeakSymbolMetric> WeakSymbols { get; init; } = [];
    public IReadOnlyList<DesktopAppMetric> Apps { get; init; } = [];
    public IReadOnlyList<TypoRecord> RecentTypos { get; init; } = [];
    public IReadOnlyList<DesktopCommitItem> RecentCommits { get; init; } = [];
}

/// <summary>
/// 在后台线程加载事件目录并通过现有 <see cref="Attributor"/> 与 <see cref="AccuracyGuard"/> 聚合桌面端看板数据。
/// </summary>
internal static class DesktopStatsService
{
    private const int DefaultMinKeySamples = 20;

    public static Task<DesktopSnapshot> LoadAsync(
        string eventsDir,
        StatsRange range,
        string? imeOverride,
        CancellationToken ct = default) =>
        Task.Run(() => LoadCore(eventsDir, range, imeOverride, ct), ct);

    internal static DesktopSnapshot LoadCore(
        string eventsDir,
        StatsRange range,
        string? imeOverride,
        CancellationToken ct = default)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly? since = range switch
        {
            StatsRange.Today => today,
            StatsRange.Days7 => today.AddDays(-6),
            _ => null,
        };

        ct.ThrowIfCancellationRequested();
        (List<string> files, IEnumerable<EventRecord> stream, int rejected) = EventLog.Read(eventsDir, since, today);
        List<EventRecord> events = [.. stream];
        ct.ThrowIfCancellationRequested();

        long todayBytes = ReadTodayLogBytes(eventsDir, today);
        AttributionResult result = new Attributor().Run(events, rejected);

        long todayKeystrokes = 0;
        long maxTodayTs = 0;
        Dictionary<DateOnly, int> dailyKeys = [];
        int[] hourlyKeys = new int[24];
        int[] hourlyTypos = new int[24];
        List<DesktopCommitItem> commits = [];

        foreach (EventRecord e in events)
        {
            if (e.Ev == "kd" && e.Sc != 0 && !KeyCatalog.IsModifier(e.Vk) && !e.HasCommandModifier)
            {
                DateTime localTime = DateTimeOffset.FromUnixTimeMilliseconds(e.Ts).ToLocalTime().DateTime;
                DateOnly day = DateOnly.FromDateTime(localTime);
                dailyKeys[day] = dailyKeys.GetValueOrDefault(day) + 1;

                if (day == today)
                {
                    todayKeystrokes++;
                    if (e.Ts > maxTodayTs)
                    {
                        maxTodayTs = e.Ts;
                    }

                    hourlyKeys[localTime.Hour]++;
                }
            }
            else if (e.Ev == "commit" && (e.Keys ?? 0) > 0 && !string.IsNullOrEmpty(e.Text))
            {
                string clean = UiTextSampler.StripPrivateUse(e.Text).Replace("\uFFFC", string.Empty, StringComparison.Ordinal).Trim();
                if (clean.Length > 0 && !UiTextSampler.LooksLikeUrl(clean))
                {
                    commits.Add(new DesktopCommitItem(e.Ts, e.Exe, clean, e.Keys!.Value));
                }
            }
        }

        Dictionary<DateOnly, int> dailyTypos = [];
        foreach (TypoRecord rec in result.Records)
        {
            DateTime localTime = DateTimeOffset.FromUnixTimeMilliseconds(rec.Ts).ToLocalTime().DateTime;
            DateOnly day = DateOnly.FromDateTime(localTime);
            dailyTypos[day] = dailyTypos.GetValueOrDefault(day) + 1;
            if (day == today)
            {
                hourlyTypos[localTime.Hour]++;
            }
        }

        List<DailyPoint> dailySeries = [.. dailyKeys.Keys
            .Union(dailyTypos.Keys)
            .OrderBy(d => d)
            .TakeLast(14)
            .Select(day =>
            {
                int k = dailyKeys.GetValueOrDefault(day);
                int t = dailyTypos.GetValueOrDefault(day);
                double acc = k > 0 ? Math.Clamp((1.0 - (double)t / k) * 100.0, 0.0, 100.0) : 100.0;
                return new DailyPoint(day, k, t, acc);
            })];

        List<HourlyPoint> hourlySeries = [.. Enumerable.Range(0, 24)
            .Select(h => new HourlyPoint(h, hourlyKeys[h], hourlyTypos[h]))];

        Dictionary<int, KeyCellMetric> keysByVk = [];
        foreach (KeyStat stat in result.Keys.Values)
        {
            List<long> sortedIki = [.. stat.Iki];
            sortedIki.Sort();
            double p50 = Attributor.Percentile(sortedIki, 0.5);
            double ratio = result.Baseline.GlobalP50 > 0 ? p50 / result.Baseline.GlobalP50 : 0;
            double? score = StatsBuilder.WeakScore(stat, ratio, DefaultMinKeySamples);
            double typoRate = stat.Presses > 0 ? (double)stat.Typos / stat.Presses : 0;
            double wpmKey = p50 > 0 ? 12000.0 / p50 : 0;

            keysByVk[stat.Vk] = new KeyCellMetric(
                Vk: stat.Vk,
                Name: KeyCatalog.Name(stat.Vk),
                Role: KeyCatalog.Role(stat.Vk),
                Presses: stat.Presses,
                Typos: stat.Typos,
                TyposUnreplaced: stat.TyposUnreplaced,
                TypoRate: typoRate,
                MedianIkiMs: p50,
                Wpm: wpmKey,
                WeakScore: score);
        }

        List<KeyCellMetric> weakLetters = [.. keysByVk.Values
            .Where(k => k.Role == KeyRole.Letter && k.Typos > 0)
            .OrderByDescending(k => k.WeakScore ?? -1.0)
            .ThenByDescending(k => k.TypoRate)
            .ThenByDescending(k => k.Typos)
            .Take(12)];

        List<WeakSymbolMetric> weakSymbols = [.. result.Symbols.Values
            .Where(s => s.Count > 0)
            .Select(s => new WeakSymbolMetric(
                s.Char,
                s.Vk,
                s.Shift,
                s.Count,
                s.Typos,
                s.Count > 0 ? (double)s.Typos / s.Count : 0))
            .OrderByDescending(s => s.Typos > 0 ? 1 : 0)
            .ThenByDescending(s => s.TypoRate)
            .ThenByDescending(s => s.Typos)
            .ThenByDescending(s => s.Presses)
            .Take(12)];

        List<DesktopAppMetric> apps = [.. result.Apps.Values
            .OrderByDescending(a => a.Keystrokes)
            .ThenBy(a => a.Exe, StringComparer.Ordinal)
            .Select(a => new DesktopAppMetric(
                a.Exe,
                a.Keystrokes,
                a.Commits,
                a.Typos,
                a.Backspaces,
                a.TextAvailable,
                a.Keystrokes > 0 ? Math.Clamp((1.0 - (double)a.Typos / a.Keystrokes) * 100.0, 0.0, 100.0) : 100.0))];

        commits.Reverse();
        if (commits.Count > 60)
        {
            commits = commits.Take(60).ToList();
        }

        List<TypoRecord> recentTypos = [.. result.Records.OrderByDescending(r => r.Ts).Take(60)];
        AccuracyReport guard = AccuracyGuard.Evaluate(result);

        double overallWpm = result.Totals.TextActiveMs > 0 && result.Totals.Chars > 0
            ? result.Totals.Chars * 12000.0 / result.Totals.TextActiveMs
            : (result.Totals.ActiveMs > 0 ? result.Totals.Keystrokes * 12000.0 / result.Totals.ActiveMs : 0);

        string resolvedIme = !string.IsNullOrWhiteSpace(imeOverride) && !string.Equals(imeOverride, "auto", StringComparison.OrdinalIgnoreCase)
            ? imeOverride
            : (result.Meta.TryGetValue("ime", out string? metaIme) ? metaIme : ImeConfig.Describe());

        int daysCount = Math.Max(files.Count, dailyKeys.Count);

        return new DesktopSnapshot
        {
            Range = range,
            GeneratedAt = DateTime.Now,
            ImeName = resolvedIme,
            TodayLogBytes = todayBytes,
            DaysCount = daysCount,
            TotalEvents = events.Count,
            FilesCount = files.Count,
            TodayKeystrokesFromDisk = todayKeystrokes,
            MaxTodayEventTs = maxTodayTs,
            Keystrokes = result.Totals.Keystrokes,
            ActiveMs = result.Totals.ActiveMs,
            TextActiveMs = result.Totals.TextActiveMs,
            Wpm = overallWpm,
            AccuracyPct = guard.AccuracyPercent,
            Typos = result.Totals.Typos,
            Commits = result.Totals.Commits,
            Chars = result.Totals.Chars,
            PinyinErrors = result.Totals.PinyinErrors,
            SelectionErrors = result.Totals.SelectionErrors,
            AccuracyReport = guard,
            DailySeries = dailySeries,
            HourlySeries = hourlySeries,
            KeysByVk = keysByVk,
            WeakLetters = weakLetters,
            WeakSymbols = weakSymbols,
            Apps = apps,
            RecentTypos = recentTypos,
            RecentCommits = commits,
        };
    }

    private static long ReadTodayLogBytes(string eventsDir, DateOnly today)
    {
        string stem = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (string name in new[] { $"{stem}.jsonl", $"{stem}.jsonl.gz" })
        {
            string p = Path.Combine(eventsDir, name);
            if (File.Exists(p))
            {
                try
                {
                    return new FileInfo(p).Length;
                }
                catch (IOException)
                {
                    return 0;
                }
            }
        }

        return 0;
    }
}
