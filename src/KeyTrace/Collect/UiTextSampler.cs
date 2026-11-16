using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace KeyTrace.Collect;

/// <summary>
/// 上屏文字采集：定时读焦点控件的文本，和上一帧比对，把新增的部分作为 commit 事件。
///
/// 不用 IME 消息（WM_IME_COMPOSITION）：浏览器、Electron 走 TSF，不发这个消息。
/// 实测记事本的 ValuePattern 能读到组字中间态，Word 的 TextPattern 能读全文。
///
/// 关键细节：组字过程中文本一直在变（全拼字母加分隔符，例如 he'l），那不叫上屏。
/// 所以差分出的片段要静置 StableMs 没再变化才算 commit，否则会把拼音字母当成汉字记下来。
/// </summary>
internal sealed class UiTextSampler : IDisposable
{
    private const int IntervalMs = 150;
    private const int StableMs = 300;
    private const int MaxTextLength = 4000;
    private const int MaxCommitLength = 200;

    private static readonly ConcurrentDictionary<int, string> NameCache = new();

    private static bool TraceEnabled;
    private static string TracePath = Path.Combine(Path.GetTempPath(), "keytrace-uia.log");

    private readonly Action<string, string, int, string> _onCommit;
    private readonly Func<bool> _isPaused;
    private readonly ExclusionList _exclusions;
    private readonly Thread _thread;
    private volatile bool _stop;

    private int _lastPid = -1;
    private string _seenText = string.Empty;
    private string _confirmedText = string.Empty;
    private DateTime _seenAt = DateTime.MinValue;

    public UiTextSampler(
        Action<string, string, int, string> onCommit,
        Func<bool> isPaused,
        ExclusionList exclusions,
        bool traceText,
        string? tracePath = null)
    {
        _onCommit = onCommit;
        _isPaused = isPaused;
        _exclusions = exclusions;
        TraceEnabled = traceText;

        if (tracePath is not null)
        {
            TracePath = tracePath;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "keytrace-uia",
        };
    }

    public void Start()
    {
        Trace("sampler start");
        _thread.Start();
    }

    private void Loop()
    {
        Trace("thread alive");

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
            catch (Exception ex)
            {
                // UIA 在目标程序卡死时可能抛任何东西，一次失败不该终止采集
                Trace("EX " + ex.GetType().Name + ": " + ex.Message);
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
            // 换控件/换应用：直接立基线，不把整篇已有内容当成一次输入
            Trace($"baseline {exe} pid={pid} len={text.Length}");
            _lastPid = pid;
            _seenText = text;
            _confirmedText = text;
            _seenAt = DateTime.UtcNow;
            return;
        }

        if (text != _seenText)
        {
            // 还在变，说明组字或输入没结束，先记时间戳
            _seenText = text;
            _seenAt = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _seenAt).TotalMilliseconds < StableMs || text == _confirmedText)
        {
            return;
        }

        string added = Diff(_confirmedText, text);

        // 整篇被替换（比如切换文档）不算输入，只更新基线
        _confirmedText = text;

        if (added.Length == 0)
        {
            return;
        }

        // 中文模式下，纯 ASCII 的增量是组字中间态（拼音和分隔符），不是上屏。
        // 用时间阈值区分不了：组字时会停下来选字，一停就"稳定"了。
        string ev = "commit";
        if (IsAsciiOnly(added) && ImeState.IsChineseMode())
        {
            ev = "comp";
        }

        Trace($"{ev} [{added}] from {exe}");
        _onCommit(added, exe, pid, ev);
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

    private static bool IsAsciiOnly(string text)
    {
        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
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

    private static void Trace(string message)
    {
        if (!TraceEnabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch (Exception)
        {
        }
    }

    private void Reset()
    {
        _lastPid = -1;
        _seenText = string.Empty;
        _confirmedText = string.Empty;
        _seenAt = DateTime.MinValue;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
