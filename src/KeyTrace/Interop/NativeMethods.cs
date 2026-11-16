using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyTrace.Interop;

/// <summary>
/// 采集层需要的 Win32 声明。
/// 刻意只声明用得上的几个：不引入 CsWin32 之类的代码生成工具，
/// 也不依赖任何第三方包——这是个要用户信任的键盘工具，"全部代码都能一眼读完"本身是特性。
/// </summary>
internal static class NativeMethods
{
    // ---------- 窗口消息 ----------
    internal const int WM_INPUT = 0x00FF;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_DESTROY = 0x0002;
    internal const int WM_TIMER = 0x0113;

    // ---------- Raw Input ----------
    /// <summary>即使窗口不在前台也接收输入（后台采集的关键）。</summary>
    internal const uint RIDEV_INPUTSINK = 0x00000100;

    internal const uint RID_INPUT = 0x10000003;
    internal const uint RIM_TYPEKEYBOARD = 1;
    internal const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    internal const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    /// <summary>Flags 里这一位置 1 表示"松开"，0 表示"按下"。</summary>
    internal const ushort RI_KEY_BREAK = 0x01;

    // ---------- 窗口样式 ----------
    internal const uint WS_POPUP = 0x80000000;

    // ---------- 进程访问权限 ----------
    /// <summary>只读进程信息的最小权限，不需要 PROCESS_QUERY_INFORMATION 那么高。</summary>
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    /// <summary>
    /// 真实结构里 Header 后面是个 union（键盘 / 鼠标 / HID）。
    /// 顺序布局把 Keyboard 紧接在 Header 之后，正好对齐 union 的起点，够用。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUT
    {
        public RAWINPUTHEADER Header;
        public RAWKEYBOARD Keyboard;
    }

    // ---------- user32 ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>返回值最高位为 1 表示该键当前处于按下状态。</summary>
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // ---------- kernel32 ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
