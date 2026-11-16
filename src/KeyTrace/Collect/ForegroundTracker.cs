using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeyTrace.Interop;

namespace KeyTrace.Collect;

/// <summary>
/// 取"事件发生时前台是哪个程序"。
/// 按 PID 缓存进程名：OpenProcess + QueryFullProcessImageName 每次都是系统调用，
/// 每按一个键都做一遍会明显拖慢采集。
/// </summary>
internal sealed class ForegroundTracker
{
    private const string Unknown = "unknown";

    private readonly Dictionary<uint, string> _cache = new();

    public (uint Pid, string Exe) Current()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return (0, Unknown);
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return (0, Unknown);
        }

        if (_cache.TryGetValue(pid, out string? cached))
        {
            return (pid, cached);
        }

        string name = Resolve(pid);
        _cache[pid] = name;
        return (pid, name);
    }

    private static string Resolve(uint pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            // 管理员权限的程序、受保护进程都会走到这里
            return Unknown;
        }

        try
        {
            uint size = 1024;
            StringBuilder buffer = new((int)size);
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Path.GetFileName(buffer.ToString())
                : Unknown;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
