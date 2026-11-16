using System;
using System.IO;
using System.Text;
using KeyTrace.Collect;

namespace KeyTrace;

internal static class Program
{
    private const string Version = "0.0.1";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = TryUseUtf8();

        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        switch (args[0])
        {
            case "-h":
            case "--help":
                PrintHelp();
                return 0;

            case "--version":
                Console.WriteLine($"keytrace {Version}");
                return 0;

            case "collect":
                return Collect(args[1..]);

            default:
                Console.Error.WriteLine($"未知命令：{args[0]}");
                Console.Error.WriteLine("运行 keytrace --help 查看可用命令。");
                return 1;
        }
    }

    /// <summary>
    /// 中文 Windows 的控制台默认是 GBK(936)，而源码是 UTF-8，
    /// 不显式切换会在部分终端里输出乱码。重定向到管道/文件时设置可能抛
    /// IOException（此时编码不重要，忽略即可），所以包一层 try。
    /// </summary>
    private static Encoding TryUseUtf8()
    {
        try
        {
            Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8;
            return utf8;
        }
        catch (IOException)
        {
            return Console.OutputEncoding;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            keytrace —— 本地打字数据采集与练习材料生成

            用法：
              keytrace <命令> [选项]

            命令：
              collect    开始录制击键（阶段 1 实现）
              report     生成统计报告（阶段 1 实现）
              export     导出为其他工具的格式（阶段 5 实现）

            选项：
              -h, --help    显示本帮助
              --version     显示版本

            数据位置：
              默认写入 %USERPROFILE%\.keytrace\events\YYYY-MM-DD.jsonl
              全部数据只存本地，不联网。
            """);
    }

    /// <summary>
    /// 采集入口。跑到 Ctrl+C 为止，退出时保证缓冲区落盘。
    /// 返回 0 表示正常结束（不是异常退出），调用方脚本据此判断。
    /// </summary>
    private static int Collect(string[] args)
    {
        string? outDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                outDir = args[i + 1];
                i++;
            }
        }

        string target = outDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".keytrace",
            "events");

        using EventWriter writer = new(target);
        using RawInputCollector collector = new(writer);

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // 不要被 Ctrl+C 直接杀掉，让消息循环自己退出
            collector.RequestStop();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            Console.WriteLine($"录制中 → {target}");
            Console.WriteLine("按 Ctrl+C 结束。");
            Console.WriteLine();

            collector.Run();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            writer.Flush();
        }

        Console.WriteLine();
        Console.WriteLine($"已停止。共记录 {collector.EventCount} 个事件。");
        return 0;
    }
}
