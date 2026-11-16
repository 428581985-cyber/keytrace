using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// </summary>
internal sealed class RawInputCollector : IDisposable
{
    private const string WindowClass = "KeyTraceRawInputSink";
    private const int RawBufferSize = 128;

    /// <summary>落盘间隔。见 WndProc 里 WM_TIMER 的注释。</summary>
    private const uint FlushIntervalMs = 1_000;
    private const uint FlushTimerId = 1;

    private readonly EventWriter _writer;
    private readonly SessionTracker _sessions = new();
    private readonly ForegroundTracker _foreground = new();

    /// <summary>单调时钟，只用来算间隔；和墙钟的偏差由 ts 字段承担。</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>必须作为字段持有：委托被 GC 回收后，原生回调会指向已释放的存根而崩溃。</summary>
    private readonly NativeMethods.WndProcDelegate _wndProc;

    private readonly IntPtr _rawBuffer = Marshal.AllocHGlobal(RawBufferSize);

    private IntPtr _hwnd;
    private bool _running;
    private bool _disposed;

    public RawInputCollector(EventWriter writer)
    {
        _writer = writer;
        _wndProc = OnWndProc;
    }

    public long EventCount { get; private set; }

    public void Run()
    {
        CreateWindowHost();
        try
        {
            RegisterKeyboard();
            NativeMethods.SetTimer(_hwnd, new UIntPtr(FlushTimerId), FlushIntervalMs, IntPtr.Zero);

            _running = true;
            while (_running && NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            _writer.Flush();
        }
    }

    /// <summary>从 Ctrl+C 处理器（另一个线程）调用，所以走 PostMessage 而不是直接改状态。</summary>
    public void RequestStop()
    {
        _running = false;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
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

            // 定时落盘。为什么不能"下一条按键到来时顺便 flush"：
            // 打完最后一个字就去开会、或者进程被杀，那批数据永远等不到下一次按键，会整批丢掉。
            // 定时器由窗口消息驱动，独立于有没有按键，1 秒最多丢 1 秒的数据。
            case NativeMethods.WM_TIMER:
                _writer.Flush();
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
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

    private void Record(NativeMethods.RAWKEYBOARD kb)
    {
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

        if (EventCount % 20 == 0)
        {
            Console.Write($"\r已记录 {EventCount} 个事件");
        }
    }

    /// <summary>
    /// 从当前按键状态反推修饰键，对应事件格式里的 mods 位图。
    /// </summary>
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

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        Marshal.FreeHGlobal(_rawBuffer);
    }
}
