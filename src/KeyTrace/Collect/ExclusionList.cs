using System;
using System.Collections.Generic;

namespace KeyTrace.Collect;

/// <summary>
/// 采集排除名单。命中的进程既不记击键、也不读文本。
///
/// 这是采集层的责任，不能交给使用者自觉：全局采集看到的是屏幕上的一切，
/// 密码管理器、网银、输入主密码的瞬间都会被读进来。
/// </summary>
internal sealed class ExclusionList
{
    private static readonly string[] BuiltIn =
    [
        "1password.exe",
        "keepass.exe",
        "keepassxc.exe",
        "bitwarden.exe",
        "lastpass.exe",
        "dashlane.exe",
        "enpass.exe",
        "nordpass.exe",
        "authenticator.exe",
    ];

    private readonly object _gate = new();
    private List<string> _extra = [];
    private volatile HashSet<string> _names = new(BuiltIn, StringComparer.OrdinalIgnoreCase);

    public ExclusionList(IEnumerable<string>? extra = null)
    {
        SetExtra(extra);
    }

    public static IReadOnlyList<string> BuiltInNames => BuiltIn;

    public IReadOnlyList<string> ExtraNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _extra];
            }
        }
    }

    public void SetExtra(IEnumerable<string>? extra)
    {
        lock (_gate)
        {
            HashSet<string> next = new(BuiltIn, StringComparer.OrdinalIgnoreCase);
            List<string> normalizedExtra = [];

            if (extra is not null)
            {
                HashSet<string> seenExtra = new(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in extra)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    string trimmed = raw.Trim();
                    if (next.Add(trimmed) && seenExtra.Add(trimmed))
                    {
                        normalizedExtra.Add(trimmed);
                    }
                }
            }

            _extra = normalizedExtra;
            _names = next;
        }
    }

    public bool IsExcluded(string? exe)
    {
        if (exe is null)
        {
            return false;
        }

        HashSet<string> snapshot = _names;
        return snapshot.Contains(exe);
    }
}
