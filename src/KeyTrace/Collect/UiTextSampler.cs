using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>刚切到新控件时的加密采样间隔，抢在用户打字之前把基线立住。</summary>
    private const int FastIntervalMs = 50;

    /// <summary>切换后加密采样的持续时间。</summary>
    private const int FastWindowMs = 800;
    private const int StableMs = 300;
    private const int MaxTextLength = 4000;
    private const int MaxCommitLength = 200;

    /// <summary>纯 ASCII 增量挂起多久后仍无汉字出现，才认定为真上屏。</summary>
    private const int AsciiHoldMs = 1800;


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
    private string _pendingText = string.Empty;
    private DateTime _seenAt = DateTime.MinValue;
    private DateTime _pendingSince = DateTime.MinValue;
    private DateTime _fastUntil = DateTime.MinValue;
    private AutomationElement? _cachedElement;
    private int _cachedPid = -1;

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
            Thread.Sleep(DateTime.UtcNow < _fastUntil ? FastIntervalMs : IntervalMs);

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

        AutomationElement? target = ResolveTextElement(focused, pid);
        if (target is null)
        {
            Trace($"no text element in {exe}");
            Reset();
            return;
        }

        string? text = ReadText(target);
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
            _cachedElement = null;
            _cachedPid = -1;
            // 换控件/换应用：直接立基线，不把整篇已有内容当成一次输入
            Trace($"baseline {exe} pid={pid} len={text.Length}");
            _fastUntil = DateTime.UtcNow.AddMilliseconds(FastWindowMs);
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

        if (added.Length == 0)
        {
            // 纯删除或整篇替换：只推进基线，不算输入
            _confirmedText = text;
            _pendingSince = DateTime.MinValue;
            return;
        }

        if (ContainsNonAscii(added))
        {
            // 出现汉字（或中文标点）：之前挂着的拼音说明被替换掉了，直接丢掉
            if (_pendingSince != DateTime.MinValue)
            {
                Trace($"drop held [{_pendingText}]");
            }

            _confirmedText = text;
            _pendingSince = DateTime.MinValue;
            _pendingText = string.Empty;
            Trace($"commit [{added}] from {exe}");
            _onCommit(added, exe, pid, "commit");
            return;
        }

        // 纯 ASCII：可能是组字中的拼音，也可能是真的在打英文。
        // 挂起来，等它被汉字替换（丢弃）或者超时（认定为上屏）。
        if (_pendingSince == DateTime.MinValue)
        {
            _pendingSince = DateTime.UtcNow;
            _pendingText = added;
            Trace($"hold [{added}]");
            return;
        }

        _pendingText = added;

        if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds < AsciiHoldMs)
        {
            return;
        }

        _confirmedText = text;
        _pendingSince = DateTime.MinValue;
        Trace($"commit(held timeout) [{added}] from {exe}");
        _onCommit(added, exe, pid, "commit");
        _pendingText = string.Empty;
    }

    /// <summary>
    /// 焦点元素本身常常不是输入控件（微信的焦点停在 Window 层，浏览器的焦点在 Document 上）。
    /// 这时从顶层窗口往下找三层，捞一个有文本的 Edit / Document。
    ///
    /// Chromium 的可访问性树是惰性构建的，第一次请求可能什么都拿不到，所以找到后缓存，
    /// 没找到就下一轮再试。
    /// </summary>
    private AutomationElement? ResolveTextElement(AutomationElement focused, int pid)
    {
        if (ReadText(focused) is not null)
        {
            return focused;
        }

        if (_cachedElement is not null && _cachedPid == pid)
        {
            try
            {
                if (ReadText(_cachedElement) is not null)
                {
                    return _cachedElement;
                }
            }
            catch (ElementNotAvailableException)
            {
                _cachedElement = null;
            }
        }

        AutomationElement? found = SearchForTextElement(focused);
        if (found is not null)
        {
            Trace($"found text element in {NameOf(pid)}");
            _cachedElement = found;
            _cachedPid = pid;
        }

        return found;
    }

    /// <summary>广度优先往下找，节点数和层数都封顶，避免在 UI 复杂的程序上卡住采样线程。</summary>
    private static AutomationElement? SearchForTextElement(AutomationElement start)
    {
        const int maxDepth = 4;
        const int maxNodes = 300;

        Queue<(AutomationElement Element, int Depth)> queue = new();
        queue.Enqueue((start, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < maxNodes)
        {
            (AutomationElement element, int depth) = queue.Dequeue();
            visited++;

            if (depth > 0 && ReadText(element) is not null)
            {
                return element;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            AutomationElement? child;
            try
            {
                child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            }
            catch (Exception)
            {
                continue;
            }

            while (child is not null && visited + queue.Count < maxNodes)
            {
                queue.Enqueue((child, depth + 1));
                try
                {
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        return null;
    }

    private static string? ReadText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
            {
                string value = ((ValuePattern)valuePattern).Current.Value;

                // Electron 和浏览器的 Document 会把页面 URL 当成 Value 返回，
                // 那不是用户输入的内容。不排掉的话会误以为"读到了文本"，
                // 于是永远不去下层找真正的输入框。
                if (!LooksLikeUrl(value))
                {
                    return value;
                }
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
    internal static string Diff(string oldText, string newText)
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

    internal static bool LooksLikeUrl(string text) =>
        text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static bool ContainsNonAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                return true;
            }
        }

        return false;
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
        _pendingText = string.Empty;
        _seenAt = DateTime.MinValue;
        _pendingSince = DateTime.MinValue;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
