namespace KeyTrace.Collect;

/// <summary>
/// 会话切分。规则见 <c>docs/event-schema.md</c>：换进程 / 空闲超过 5 秒 / 跨天，都算新会话。
/// 为什么必须切：统计"每分钟字数"要有明确的分母，否则中间去倒杯水回来，那一分钟的速度就是假的。
/// </summary>
internal sealed class SessionTracker
{
    private const long IdleGapMs = 5_000;

    /// <summary>
    /// 本进程的启动时刻（时分秒），拼进 sid 里。
    /// 不这么做的话：进程重启后序号从 1 重新数，会和上一轮写在同一份日志里的
    /// <c>20261116-1</c> 撞号，按 sid 分组的统计就会把两次运行混成一段。
    /// </summary>
    private readonly string _runStamp = DateTime.Now.ToString("HHmmss");

    private string? _session;
    private string? _lastExe;
    private long _lastMono;
    private DateOnly _lastDay;
    private int _sequence;

    /// <summary>当前会话 ID，形如 <c>20261116-220105-2</c>（2026-11-16 22:01:05 启动后的第 2 次会话）。</summary>
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
            _session = $"{today.ToString("yyyyMMdd")}-{_runStamp}-{_sequence}";
        }

        _lastExe = exe;
        _lastMono = mono;
        return _session!;
    }
}
