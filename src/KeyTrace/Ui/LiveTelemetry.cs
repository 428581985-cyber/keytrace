using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KeyTrace.Analysis;
using KeyTrace.Collect;

namespace KeyTrace.Ui;

internal sealed record LiveFeedItem(
    long Ts,
    string Exe,
    string Kind,
    string? Text,
    int Keys,
    string? WrongKey,
    string? RightKey);

internal sealed record LiveTelemetrySnapshot(
    long TodayKeystrokes,
    long SessionKeystrokes,
    int LiveWpm,
    string CurrentExe,
    long? LastEventTs,
    string? LastCorrectionText,
    string? LastCorrectionCompact,
    long? LastCorrectionTs,
    string? CurrentIme,
    IReadOnlyList<LiveFeedItem> Feed);

/// <summary>
/// 线程安全的实时打字遥测状态。
/// 采集钩子只往这里推轻量事件，UI 线程用 DispatcherTimer 定期拉取快照，
/// 绝不在击键热路径上做跨线程封送或文件读取。
/// </summary>
internal sealed class LiveTelemetry
{
    private const long ActiveWindowMs = 30_000;
    private const long IdleZeroWpmMs = 15_000;
    private const long MaxStepGapMs = 3_000;
    private const long CorrectionWindowMs = 3_000;
    private const long CorrectionHighlightMs = 8_000;
    private const int MaxFeedItems = 80;
    private const int VkBackspace = 8;

    private readonly object _gate = new();
    private readonly List<long> _windowTimestamps = [];
    private readonly List<long> _liveKeyTimestamps = [];
    private readonly LinkedList<LiveFeedItem> _feed = new();

    private DateOnly _currentDay = DateOnly.FromDateTime(DateTime.Now);
    private long _seededTodayBase;
    private long _sessionKeystrokes;
    private string _currentExe = "";
    private long? _lastEventTs;
    private string? _currentIme;

    private (char KeyChar, long Ts, string Sid)? _lastTypableKey;
    private (char DeletedChar, long BsTs, string Sid, int BsCount)? _pendingCorrection;
    private string? _lastCorrectionText;
    private string? _lastCorrectionCompact;
    private long? _lastCorrectionTs;

    private string? _tailedFilePath;
    private long _tailOffset;
    private bool _tailInitialized;

    public void SeedFromTodayCount(long keystrokes, long? upToTs = null)
    {
        lock (_gate)
        {
            RollDayIfNeeded(DateOnly.FromDateTime(DateTime.Now));
            _seededTodayBase = Math.Max(0, keystrokes);
            if (upToTs is long cutoff)
            {
                _liveKeyTimestamps.RemoveAll(t => t <= cutoff);
            }
            else
            {
                _liveKeyTimestamps.Clear();
            }
        }
    }

    public void SeedRecentFeed(IEnumerable<LiveFeedItem> items)
    {
        lock (_gate)
        {
            if (_feed.Count >= MaxFeedItems)
            {
                return;
            }

            List<LiveFeedItem> merged = [.. _feed];
            HashSet<(long Ts, string Kind, string Exe)> seen = [.. merged.Select(x => (x.Ts, x.Kind, x.Exe))];

            foreach (LiveFeedItem item in items)
            {
                if (seen.Add((item.Ts, item.Kind, item.Exe)))
                {
                    merged.Add(item);
                }
            }

            merged.Sort((a, b) => b.Ts.CompareTo(a.Ts));
            _feed.Clear();
            foreach (LiveFeedItem item in merged.Take(MaxFeedItems))
            {
                _feed.AddLast(item);
            }
        }
    }

    public void Observe(KeyEvent ev) =>
        ObserveCore(ev.Ts, ev.Ev, ev.Vk, ev.Mods, ev.Exe, ev.Sid, ev.Text, ev.Keys);

    public void ObserveRecord(EventRecord ev) =>
        ObserveCore(ev.Ts, ev.Ev, ev.Vk, ev.Mods, ev.Exe, ev.Sid, ev.Text, ev.Keys);

    /// <summary>
    /// 独立只读视图模式用：按文件偏移增量追踪当日 .jsonl，保持计数、实时 WPM 与心跳跳动。
    /// 初始化首次调用直接定位到当前文件末尾（配合 SeedRecentFeed），避免重复计数历史击键。
    /// </summary>
    public void PollTodayFile(string eventsDirectory)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        string path = Path.Combine(eventsDirectory, $"{today.ToString("yyyy-MM-dd")}.jsonl");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!string.Equals(_tailedFilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _tailedFilePath = path;
                _tailOffset = stream.Length;
                _tailInitialized = true;
                return;
            }

            if (!_tailInitialized)
            {
                _tailOffset = stream.Length;
                _tailInitialized = true;
                return;
            }

            if (stream.Length < _tailOffset)
            {
                _tailOffset = 0;
            }

            if (stream.Length == _tailOffset)
            {
                return;
            }

            stream.Seek(_tailOffset, SeekOrigin.Begin);
            using MemoryStream chunk = new();
            stream.CopyTo(chunk);
            byte[] bytes = chunk.ToArray();
            int lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            if (lastNewline < 0)
            {
                return;
            }

            int consumed = lastNewline + 1;
            _tailOffset += consumed;

            string text = Encoding.UTF8.GetString(bytes, 0, consumed);
            using StringReader reader = new(text);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (EventRecord.TryParse(line, out EventRecord rec))
                {
                    ObserveRecord(rec);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件轮转或被系统短暂占用时跳过本轮
        }
    }

    public LiveTelemetrySnapshot Snapshot(long? nowTs = null)
    {
        long now = nowTs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            RollDayIfNeeded(DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(now).ToLocalTime().Date));
            PruneWindow(now);

            int liveWpm = ComputeLiveWpm(now);
            bool correctionFresh = _lastCorrectionTs is long cts && now - cts <= CorrectionHighlightMs;

            return new LiveTelemetrySnapshot(
                TodayKeystrokes: _seededTodayBase + _liveKeyTimestamps.Count,
                SessionKeystrokes: _sessionKeystrokes,
                LiveWpm: liveWpm,
                CurrentExe: _currentExe,
                LastEventTs: _lastEventTs,
                LastCorrectionText: correctionFresh ? _lastCorrectionText : null,
                LastCorrectionCompact: correctionFresh ? _lastCorrectionCompact : null,
                LastCorrectionTs: correctionFresh ? _lastCorrectionTs : null,
                CurrentIme: _currentIme,
                Feed: [.. _feed]);
        }
    }

    private void ObserveCore(long ts, string ev, int vk, int mods, string exe, string sid, string? text, int? keys)
    {
        lock (_gate)
        {
            long effectiveTs = ts > 0 ? ts : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            DateOnly evDay = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(effectiveTs).ToLocalTime().Date);
            RollDayIfNeeded(evDay);

            if (ev == "meta")
            {
                if (text is not null && text.StartsWith("ime=", StringComparison.Ordinal))
                {
                    _currentIme = text[4..].Trim();
                }

                return;
            }

            if (ev == "kd")
            {
                bool hasCommandMod = (mods & 0b1110) != 0;
                if (KeyCatalog.IsModifier(vk) || hasCommandMod)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(exe) && !string.Equals(exe, "keytrace", StringComparison.OrdinalIgnoreCase))
                {
                    _currentExe = exe;
                }

                _lastEventTs = Math.Max(_lastEventTs ?? 0, effectiveTs);
                _liveKeyTimestamps.Add(effectiveTs);
                _sessionKeystrokes++;

                _windowTimestamps.Add(effectiveTs);
                PruneWindow(effectiveTs);

                TrackCorrection(effectiveTs, vk, exe, sid);
                return;
            }

            if (ev == "commit")
            {
                if ((keys ?? 0) <= 0 || string.IsNullOrEmpty(text))
                {
                    return;
                }

                string clean = UiTextSampler.StripPrivateUse(text).Replace("\uFFFC", string.Empty, StringComparison.Ordinal).Trim();
                if (clean.Length == 0 || UiTextSampler.LooksLikeUrl(clean))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(exe) && !string.Equals(exe, "keytrace", StringComparison.OrdinalIgnoreCase))
                {
                    _currentExe = exe;
                }

                _lastEventTs = Math.Max(_lastEventTs ?? 0, effectiveTs);
                PushFeed(new LiveFeedItem(
                    Ts: effectiveTs,
                    Exe: exe,
                    Kind: "commit",
                    Text: clean,
                    Keys: keys!.Value,
                    WrongKey: null,
                    RightKey: null));
            }
        }
    }

    private void TrackCorrection(long ts, int vk, string exe, string sid)
    {
        if (vk == VkBackspace)
        {
            if (_lastTypableKey is { } prev && prev.Sid == sid && ts - prev.Ts <= CorrectionWindowMs)
            {
                _pendingCorrection = (prev.KeyChar, ts, sid, 1);
                _lastTypableKey = null;
            }
            else if (_pendingCorrection is { } pending && pending.Sid == sid && ts - pending.BsTs <= CorrectionWindowMs)
            {
                _pendingCorrection = (pending.DeletedChar, ts, sid, pending.BsCount + 1);
            }
            else
            {
                _pendingCorrection = null;
            }

            return;
        }

        if (TryTypableLabel(vk, out char keyChar))
        {
            if (_pendingCorrection is { } pending
                && pending.BsCount == 1
                && pending.Sid == sid
                && ts - pending.BsTs <= CorrectionWindowMs
                && pending.DeletedChar != keyChar)
            {
                _lastCorrectionText = $"误 {pending.DeletedChar} → {keyChar}";
                _lastCorrectionCompact = $"误 {pending.DeletedChar}→{keyChar}";
                _lastCorrectionTs = ts;
                PushFeed(new LiveFeedItem(
                    Ts: ts,
                    Exe: exe,
                    Kind: "correction",
                    Text: null,
                    Keys: 1,
                    WrongKey: pending.DeletedChar.ToString(),
                    RightKey: keyChar.ToString()));
            }

            _pendingCorrection = null;
            _lastTypableKey = (keyChar, ts, sid);
            return;
        }

        // 空格 / 回车 / 其他功能键打断词内纠错链路
        _lastTypableKey = null;
        _pendingCorrection = null;
    }

    private static bool TryTypableLabel(int vk, out char keyChar)
    {
        if (vk is >= 65 and <= 90)
        {
            keyChar = (char)vk;
            return true;
        }

        if (vk is >= 48 and <= 57)
        {
            keyChar = (char)vk;
            return true;
        }

        if (vk is >= 96 and <= 105)
        {
            keyChar = (char)('0' + (vk - 96));
            return true;
        }

        keyChar = default;
        return false;
    }

    private void PushFeed(LiveFeedItem item)
    {
        _feed.AddFirst(item);
        while (_feed.Count > MaxFeedItems)
        {
            _feed.RemoveLast();
        }
    }

    private void PruneWindow(long now)
    {
        long cutoff = now - ActiveWindowMs;
        while (_windowTimestamps.Count > 0 && _windowTimestamps[0] < cutoff)
        {
            _windowTimestamps.RemoveAt(0);
        }
    }

    private int ComputeLiveWpm(long now)
    {
        if (_windowTimestamps.Count < 2)
        {
            return 0;
        }

        long lastTs = _windowTimestamps[^1];
        if (now - lastTs > IdleZeroWpmMs)
        {
            return 0;
        }

        long activeMs = 0;
        int intervals = 0;
        for (int i = 1; i < _windowTimestamps.Count; i++)
        {
            long dt = _windowTimestamps[i] - _windowTimestamps[i - 1];
            if (dt > 0 && dt <= MaxStepGapMs)
            {
                activeMs += dt;
                intervals++;
            }
        }

        if (intervals < 2 || activeMs <= 0)
        {
            return 0;
        }

        double raw = intervals * 12000.0 / activeMs;
        return (int)Math.Clamp(Math.Round(raw), 0, 300);
    }

    private void RollDayIfNeeded(DateOnly day)
    {
        if (day == _currentDay)
        {
            return;
        }

        _currentDay = day;
        _seededTodayBase = 0;
        _liveKeyTimestamps.Clear();
    }
}
