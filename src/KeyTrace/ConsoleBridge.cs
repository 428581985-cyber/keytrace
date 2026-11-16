using System;
using System.IO;
using System.Text;

namespace KeyTrace;

/// <summary>
/// 本项目编译成 WinExe（不分配控制台），双击和开机自启时不会闪黑框。
/// 但这样命令行输出就没地方去了，所以启动时先尝试"借用"父进程的控制台——
/// 从终端启动时能看见输出，从资源管理器启动时静默丢弃。
/// </summary>
internal static class ConsoleBridge
{
    public static bool Attached { get; private set; }

    public static void Attach()
    {
        if (!Interop.NativeMethods.AttachConsole(Interop.NativeMethods.ATTACH_PARENT_PROCESS))
        {
            return;
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            });

            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            });

            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // 输出被重定向时设置编码会失败，不影响功能
            }

            Attached = true;
        }
        catch (IOException)
        {
            Attached = false;
        }
    }
}
