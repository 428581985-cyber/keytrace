namespace KeyTrace.Collect;

/// <summary>
/// 会话切分。规则见 <c>docs/event-schema.md</c>：换进程 / 空闲超过 5 秒 / 跨天，都算新会话。
/// 为什么必须切：统计"每分钟字数"要有明确的分母，否则中间去倒杯水回来，那一分钟的速度就是假的。
/// </summary>
internal sealed class SessionTracker
{
    private const long IdleGapMs = 5_000;

    private string? _session;
    private string? _lastExe;
    private long _lastMono;
    private DateOnly _lastDay;
    private int _sequence;

    /// <summary>当前会话 ID，形如 <c>20261116-3</c>（当天第 3 次会话）。</summary>
    public string Current => _session ?? string.Empty;

    public string Observe(string exe, long mono, DateOnly today)
    {
        bool isNew =
            _session is null ||
            today != _lastDay ||
            !string.Equals(_lastExe, exe, StringComparison.OrdinalIgnoreCase) ||
            mono - _lastMono > IdleGapMs;

        if (isNew)
        {
            if (today != _lastDay)
            {
                _sequence = 0;
                _lastDay = today;
            }

            _sequence++;
            _session = $"{today.ToString("yyyyMMdd")}-{_sequence}";
        }

        _lastExe = exe;
        _lastMono = mono;
        return _session!;
    }
}
