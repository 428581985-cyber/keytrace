using System;
using KeyTrace.Interop;

namespace KeyTrace.Control;

/// <summary>
/// 全局热键：Ctrl + Alt + P 暂停 / 继续。
/// 用 RegisterHotKey 而不是键盘钩子——热键是系统级注册，不占钩子链，
/// 也不会像低级钩子那样因为回调慢被系统卸载。
/// </summary>
internal static class GlobalHotkey
{
    public const int Id = 0xA1;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkP = 0x50;

    /// <summary>返回 false 表示组合键已被别的程序占用（比如某些截图工具）。</summary>
    public static bool Register(IntPtr hwnd) =>
        NativeMethods.RegisterHotKey(hwnd, Id, ModControl | ModAlt, VkP);

    public static void Unregister(IntPtr hwnd) => NativeMethods.UnregisterHotKey(hwnd, Id);

    public static string Description => "Ctrl+Alt+P";
}
