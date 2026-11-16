using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace KeyTrace.Collect;

/// <summary>
/// 上屏文字采集：定时读焦点控件的文本，和上一帧比对，把新增的部分作为 commit 事件。
///
/// 为什么不用 IME 消息（WM_IME_COMPOSITION）：浏览器、Electron 走 TSF，根本不发这个消息。
/// 已经实测：记事本的 ValuePattern 能读到组字中间态，Word 的 TextPattern 能读全文。
///
/// 读不到的应用不降级、不假装：没有 commit 事件就是没有，下游按"只有击键"处理。
/// </summary>
internal sealed class UiTextSampler : IDisposable
{
    private const int IntervalMs = 150;
    private const int MaxTextLength = 4000;
    private const int MaxCommitLength = 200;

    private static readonly ConcurrentDictionary<int, string> NameCache = new();

    private readonly Action<string, string, int> _onCommit;
    private readonly Func<bool> _isPaused;
    private readonly ExclusionList _exclusions;
    private readonly Thread _thread;
    private volatile bool _stop;

    private int _lastPid = -1;
    private string _lastText = string.Empty;

    public UiTextSampler(
        Action<string, string, int> onCommit,
        Func<bool> isPaused,
        ExclusionList exclusions)
    {
        _onCommit = onCommit;
        _isPaused = isPaused;
        _exclusions = exclusions;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "keytrace-uia",
        };
    }

    public void Start() => _thread.Start();

    private void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(IntervalMs);

            if (_isPaused())
            {
                Reset();
                continue;
            }

            try
            {
                Probe();
            }
            catch (ElementNotAvailableException)
            {
                Reset();
            }
            catch (Exception)
            {
                // UIA 在目标程序卡死时可能抛任何东西，一次失败不该终止采集
                Reset();
            }
        }
    }

    private void Probe()
    {
        AutomationElement? focused = AutomationElement.FocusedElement;
        if (focused is null)
        {
            Reset();
            return;
        }

        int pid = focused.Current.ProcessId;
        string exe = NameOf(pid);

        if (_exclusions.IsExcluded(exe))
        {
            Reset();
            return;
        }

        if (focused.Current.IsPassword)
        {
            Reset();
            return;
        }

        string? text = ReadText(focused);
        if (text is null)
        {
            Reset();
            return;
        }

        if (text.Length > MaxTextLength)
        {
            text = text.Substring(0, MaxTextLength);
        }

        if (pid != _lastPid)
        {
            // 换控件/换应用：先立基线，不把它当成一次输入
            _lastPid = pid;
            _lastText = text;
            return;
        }

        string added = Diff(_lastText, text);
        _lastText = text;

        if (added.Length > 0)
        {
            _onCommit(added, exe, pid);
        }
    }

    private static string? ReadText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
            {
                return ((ValuePattern)valuePattern).Current.Value;
            }
        }
        catch (Exception)
        {
            // 有的控件声明支持 ValuePattern 但读取会抛
        }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern))
            {
                return ((TextPattern)textPattern).DocumentRange.GetText(MaxTextLength);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>
    /// 取"新增"的那一段：公共前缀之后、公共后缀之前。
    /// 纯删除返回空（删除不是输入）；整篇被替换也返回空，避免把一次重排当成打了一大段。
    /// </summary>
    private static string Diff(string oldText, string newText)
    {
        int prefix = 0;
        int min = Math.Min(oldText.Length, newText.Length);
        while (prefix < min && oldText[prefix] == newText[prefix])
        {
            prefix++;
        }

        int endOld = oldText.Length - 1;
        int endNew = newText.Length - 1;
        while (endOld >= prefix && endNew >= prefix && oldText[endOld] == newText[endNew])
        {
            endOld--;
            endNew--;
        }

        int length = endNew - prefix + 1;
        if (length <= 0 || length > MaxCommitLength)
        {
            return string.Empty;
        }

        return newText.Substring(prefix, length);
    }

    private static string NameOf(int pid)
    {
        if (NameCache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string name;
        try
        {
            name = Process.GetProcessById(pid).ProcessName + ".exe";
        }
        catch (Exception)
        {
            name = "unknown";
        }

        NameCache[pid] = name;
        return name;
    }

    private void Reset()
    {
        _lastPid = -1;
        _lastText = string.Empty;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
