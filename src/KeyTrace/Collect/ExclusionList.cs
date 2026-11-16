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

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public ExclusionList(IEnumerable<string>? extra = null)
    {
        foreach (string name in BuiltIn)
        {
            _names.Add(name);
        }

        if (extra is null)
        {
            return;
        }

        foreach (string name in extra)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _names.Add(name.Trim());
            }
        }
    }

    public bool IsExcluded(string? exe) => exe is not null && _names.Contains(exe);
}
