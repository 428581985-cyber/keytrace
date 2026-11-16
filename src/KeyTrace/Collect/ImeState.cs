using System;
using System.Runtime.InteropServices;

namespace KeyTrace.Collect;

/// <summary>
/// 读当前输入法的中/英状态。
///
/// 为什么需要：中文模式下，输入法会把组字过程（拼音字母加分隔符）直接画进控件里，
/// UIA 读到的文本和真正上屏的汉字混在一起。分不清这两者，就会把拼音当成打出来的字。
///
/// 只读 IMM32 的转换状态；TSF 独占的应用（部分浏览器）可能拿不到上下文，
/// 那种情况返回 false，当作英文处理，由下游按"可能混入组字"容错。
/// </summary>
internal static class ImeState
{
    private const uint ImeCmodeNative = 0x0001;

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint conversion, out uint sentence);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static bool IsChineseMode()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr hImc = ImmGetContext(hwnd);
        if (hImc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return ImmGetConversionStatus(hImc, out uint conversion, out _)
                && (conversion & ImeCmodeNative) != 0;
        }
        finally
        {
            ImmReleaseContext(hwnd, hImc);
        }
    }
}
