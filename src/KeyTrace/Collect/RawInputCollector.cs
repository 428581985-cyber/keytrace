using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using KeyTrace.Control;
using KeyTrace.Interop;

namespace KeyTrace.Collect;

/// <summary>
/// 全局物理击键采集。
///
/// 机制选择：Raw Input（RIDEV_INPUTSINK），不用 WH_KEYBOARD_LL。
/// 低级钩子的回调一旦超时，Windows 会**静默卸载**你的钩子——程序还在跑，却再也收不到键；
/// Raw Input 的输入进的是消息队列，处理慢了只是排队，不会被系统踢掉。
///
/// 窗口必须是隐藏的**顶层**窗口，不能是 message-only 窗口：
/// message-only 窗口收不到 RIDEV_INPUTSINK 的 WM_INPUT。
///
/// <para>
/// 两种运行方式共用同一套窗口与采集逻辑：
/// <list type="bullet">
/// <item><c>Run()</c>——自带消息循环，给 <c>keytrace collect</c> 调试用，Ctrl+C 退出</item>
/// <item><c>Start()</c>——只建窗口不循环，消息由 WinForms 的 Application.Run 泵，给常驻用</item>
/// </list>
/// </para>
/// </summary>
internal sealed class RawInputCollector : IDisposable
{
    private const string WindowClass = "KeyTraceRawInputSink";
    private const int RawBufferSize = 128;

    /// <summary>落盘与信号检查的间隔。见 WndProc 里 WM_TIMER 的注释。</summary>
    private const uint TickIntervalMs = 1_000;
    private const uint TickTimerId = 1;

    private readonly EventWriter _writer;
    private readonly SessionTracker _sessions = new();
    private readonly ForegroundTracker _foreground = new();
    private readonly ExclusionList _exclusions;
    private readonly UiTextSampler _sampler;
    private readonly bool _captureText;
    private readonly EventWaitHandle? _stopSignal;
    private readonly EventWaitHandle? _pauseSignal;

    /// <summary>单调时钟，只用来算间隔；墙钟由 ts 字段承担。</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>必须作为字段持有：委托被 GC 回收后，原生回调会指向已释放的存根而崩溃。</summary>
    private readonly NativeMethods.WndProcDelegate _wndProc;

    private readonly IntPtr _rawBuffer = Marshal.AllocHGlobal(RawBufferSize);

    private IntPtr _hwnd;

    /// <summary>上一次上屏之后按了多少下键。用来识别"没有击键的文本变化"（粘贴、拖拽）。</summary>
    private int _keysSinceCommit;

    /// <summary>上一次写 meta 的日期。跨天要补一条，否则新文件不知道自己是什么输入法方案。</summary>
    private DateOnly _metaDay;
    private bool _hotkeyRegistered;
    private bool _running;
    private bool _disposed;

    public RawInputCollector(
        EventWriter writer,
        EventWaitHandle? stopSignal = null,
        EventWaitHandle? pauseSignal = null,
        ExclusionList? exclusions = null,
        bool captureText = true,
        bool traceText = false)
    {
        _writer = writer;
        _stopSignal = stopSignal;
        _pauseSignal = pauseSignal;
        _exclusions = exclusions ?? new ExclusionList();
        _captureText = captureText;
        _wndProc = OnWndProc;
        _sampler = new UiTextSampler(
            OnCommitText, () => IsPaused, _exclusions, traceText,
            traceText ? Path.Combine(writer.OutputDirectory, "uia-debug.log") : null);
    }

    /// <summary>暂停时切换回调，参数为"是否已暂停"。</summary>
    public event Action<bool>? PauseChanged;

    /// <summary>收到停止请求时触发。</summary>
    public event Action? Stopped;

    public long EventCount { get; private set; }

    public string DataDirectory => _writer.OutputDirectory;

    /// <summary>热键被别的程序占用时为 false，此时只能靠托盘菜单暂停。</summary>
    public bool HotkeyRegistered => _hotkeyRegistered;

    public bool IsPaused => _pauseSignal?.WaitOne(0) ?? false;

    /// <summary>建窗口、注册设备、装热键。不进入消息循环。</summary>
    public void Start()
    {
        CreateWindowHost();
        RegisterKeyboard();
        NativeMethods.SetTimer(_hwnd, new UIntPtr(TickTimerId), TickIntervalMs, IntPtr.Zero);

        _hotkeyRegistered = GlobalHotkey.Register(_hwnd);
        _running = true;

        WriteSessionMeta();

        if (_captureText)
        {
            _sampler.Start();
        }
    }

    /// <summary>调试模式的阻塞循环，Ctrl+C 与 stop 信号都能让它退出。</summary>
    public void Run()
    {
        Start();
        while (_running && NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        _writer.Flush();
    }

    public void RequestStop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _writer.Flush();
        Stopped?.Invoke();

        // 调试模式下有人在泵自己的循环，发个 WM_CLOSE 让它自然收尾
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void TogglePause()
    {
        if (_pauseSignal is null)
        {
            return;
        }

        bool nowPaused = !IsPaused;
        if (nowPaused)
        {
            _pauseSignal.Set();
        }
        else
        {
            _pauseSignal.Reset();
        }

        _writer.Flush();
        PauseChanged?.Invoke(nowPaused);
    }

    private void CreateWindowHost()
    {
        IntPtr instance = NativeMethods.GetModuleHandle(null);

        NativeMethods.WNDCLASSEX wc = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = WindowClass,
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx 失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, WindowClass, "keytrace", NativeMethods.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx 失败，错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    private void RegisterKeyboard()
    {
        NativeMethods.RAWINPUTDEVICE[] devices =
        [
            new()
            {
                UsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                Usage = NativeMethods.HID_USAGE_GENERIC_KEYBOARD,
                Flags = NativeMethods.RIDEV_INPUTSINK,
                Target = _hwnd,
            },
        ];

        if (!NativeMethods.RegisterRawInputDevices(
                devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException(
                $"RegisterRawInputDevices 失败，错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_INPUT:
                HandleRawInput(lParam);
                return IntPtr.Zero;

            // 定时器干两件事：落盘 + 检查停止信号。
            // 为什么不能"下一条按键到来时顺便 flush"：打完最后一个字就去开会、或者进程被杀，
            // 那批数据永远等不到下一次按键，会整批丢掉。
            // 停止信号用轮询而不是 WaitOne 阻塞，是因为不能被阻塞在采集线程上。
            case NativeMethods.WM_TIMER:
                _writer.Flush();

                if (DateOnly.FromDateTime(DateTime.Now) != _metaDay)
                {
                    WriteSessionMeta();
                }

                if (_stopSignal?.WaitOne(0) == true)
                {
                    RequestStop();
                }

                return IntPtr.Zero;

            case NativeMethods.WM_HOTKEY:
                if (wParam.ToInt32() == GlobalHotkey.Id)
                {
                    TogglePause();
                }

                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                // 调试模式靠这条退出；常驻模式下没有自己的循环，PostQuitMessage 是空操作
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void HandleRawInput(IntPtr lParam)
    {
        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        uint size = 0;

        // 第一次调用只问"要多大"，不写数据
        if (NativeMethods.GetRawInputData(
                lParam, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0)
        {
            return;
        }

        if (size == 0 || size > RawBufferSize)
        {
            return;
        }

        if (NativeMethods.GetRawInputData(
                lParam, NativeMethods.RID_INPUT, _rawBuffer, ref size, headerSize) != size)
        {
            return;
        }

        NativeMethods.RAWINPUT raw = Marshal.PtrToStructure<NativeMethods.RAWINPUT>(_rawBuffer);
        if (raw.Header.Type != NativeMethods.RIM_TYPEKEYBOARD)
        {
            return;
        }

        Record(raw.Keyboard);
    }

    /// <summary>
    /// 每次启动写一条 meta 事件，记录当时的输入法方案。
    /// 全拼和双拼的键位负担完全不同，没有这一条，历史数据没法和后来的对上。
    /// </summary>
    private void WriteSessionMeta()
    {
        DateTime now = DateTime.Now;
        DateOnly today = DateOnly.FromDateTime(now);
        long mono = _clock.ElapsedMilliseconds;

        KeyEvent ev = new()
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Mono = mono,
            Ev = "meta",
            Text = "ime=" + ImeConfig.Describe(),
            Src = "registry",
            Exe = "keytrace",
            Sid = _sessions.Observe("keytrace", mono, today),
        };

        _writer.Write(ev.ToJsonLine(), today);
        _metaDay = today;
    }

    /// <summary>
    /// UIA 采样线程的回调：把新增文本写成 commit 事件。
    /// 注意这个方法跑在采样线程上，和消息循环线程并发写同一个文件，
    /// 所以 EventWriter 内部有锁。
    /// </summary>
    private void OnCommitText(string text, string exe, int pid, string eventName)
    {
        if (!_running || IsPaused)
        {
            return;
        }

        long mono = _clock.ElapsedMilliseconds;
        DateTime now = DateTime.Now;
        DateOnly today = DateOnly.FromDateTime(now);
        string sid = _sessions.Observe(exe, mono, today);

        KeyEvent ev = new()
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Mono = mono,
            Ev = eventName,
            Text = text,
            Src = "uia",
            Keys = _keysSinceCommit,
            Pid = pid,
            Exe = exe,
            Sid = sid,
        };

        _writer.Write(ev.ToJsonLine(), today);
        EventCount++;
        _keysSinceCommit = 0;
    }

    private void Record(NativeMethods.RAWKEYBOARD kb)
    {
        // 暂停期间一个事件都不记——连键码都不记，这是"暂停"该有的样子
        if (!_running || IsPaused)
        {
            return;
        }

        // 255 是输入法用来查询 Shift 状态的伪按键，不是真实击键
        if (kb.VKey is 0 or 0xFF)
        {
            return;
        }

        bool isUp = (kb.Flags & NativeMethods.RI_KEY_BREAK) != 0;
        long mono = _clock.ElapsedMilliseconds;
        DateTime now = DateTime.Now;
        DateOnly today = DateOnly.FromDateTime(now);

        (uint pid, string exe) = _foreground.Current();

        // 排除名单里的程序（密码管理器、网银等）连键码都不记
        if (_exclusions.IsExcluded(exe))
        {
            return;
        }

        string sid = _sessions.Observe(exe, mono, today);

        KeyEvent ev = new()
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Mono = mono,
            Ev = isUp ? "ku" : "kd",
            Vk = kb.VKey,
            Sc = kb.MakeCode,
            Mods = ReadModifiers(),
            Pid = (int)pid,
            Exe = exe,
            Sid = sid,
        };

        _writer.Write(ev.ToJsonLine(), today);
        EventCount++;
        _keysSinceCommit++;
    }

    /// <summary>从当前按键状态反推修饰键，对应事件格式里的 mods 位图。</summary>
    private static int ReadModifiers()
    {
        const int VK_SHIFT = 0x10;
        const int VK_CONTROL = 0x11;
        const int VK_MENU = 0x12;
        const int VK_LWIN = 0x5B;

        int mods = 0;
        if (IsDown(VK_SHIFT)) mods |= 1;
        if (IsDown(VK_CONTROL)) mods |= 2;
        if (IsDown(VK_MENU)) mods |= 4;
        if (IsDown(VK_LWIN)) mods |= 8;
        return mods;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sampler.Dispose();
        _writer.Flush();

        if (_hwnd != IntPtr.Zero)
        {
            if (_hotkeyRegistered)
            {
                GlobalHotkey.Unregister(_hwnd);
            }

            NativeMethods.KillTimer(_hwnd, new UIntPtr(TickTimerId));
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        Marshal.FreeHGlobal(_rawBuffer);
    }
}
