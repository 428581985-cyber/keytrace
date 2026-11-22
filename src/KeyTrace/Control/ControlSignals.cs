using System;
using System.IO;
using System.Threading;

namespace KeyTrace.Control;

/// <summary>
/// 跨进程控制通道。刻意不用命名管道：日常只传停/启/显三个布尔信号。
/// 高权限计划任务与普通权限用户进程之间，内核命名事件会受默认 DACL 限制；
/// 因此唤醒主窗口同时提供基于用户可写目录的轻量触发文件（.show-window）作为跨 UAC 兜底。
/// </summary>
internal static class ControlSignals
{
    private const string StopName = @"Local\KeyTrace.Stop";
    private const string PauseName = @"Local\KeyTrace.Pause";
    private const string ShowName = @"Local\KeyTrace.Show";
    private const string InstanceName = @"Local\KeyTrace.Instance";

    /// <summary>常驻端创建内核对象。重复创建同名对象会拿到已存在的那个，所以要先 Reset 清掉上次残留的状态。</summary>
    public static (EventWaitHandle Stop, EventWaitHandle Pause, EventWaitHandle Show) CreateServerSide()
    {
        // initialState 传 true 再 Reset，是为了避免"第一次创建"和"打开已存在"两种路径行为不一致
        EventWaitHandle stop = new(true, EventResetMode.ManualReset, StopName);
        stop.Reset();

        EventWaitHandle pause = new(true, EventResetMode.ManualReset, PauseName);
        pause.Reset();

        EventWaitHandle show = new(true, EventResetMode.ManualReset, ShowName);
        show.Reset();

        return (stop, pause, show);
    }

    /// <summary>返回 null 表示已经有一个实例在跑（含高权限实例导致的访问受限）。</summary>
    public static Mutex? TryAcquireInstance()
    {
        try
        {
            Mutex mutex = new(initiallyOwned: true, InstanceName, out bool createdNew);
            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string ShowTriggerPath(string baseDirectory) => Path.Combine(baseDirectory, ".show-window");

    /// <summary>
    /// 尝试唤醒已有常驻端的主窗口：优先内核事件；若跨 UAC 受阻则写 .show-window 并等待后台消费。
    /// 返回 false 说明后台无响应（多半是尚未升级的无窗口旧版本）。
    /// </summary>
    public static bool RequestShowViaFile(string baseDirectory, int waitMs = 700)
    {
        if (SignalShow())
        {
            return true;
        }

        string path = ShowTriggerPath(baseDirectory);
        try
        {
            Directory.CreateDirectory(baseDirectory);
            File.WriteAllText(path, Environment.ProcessId.ToString());

            int elapsed = 0;
            while (elapsed < waitMs)
            {
                Thread.Sleep(50);
                elapsed += 50;
                if (!File.Exists(path))
                {
                    return true;
                }
            }

            TryDeleteFile(path);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(path);
            return false;
        }
    }

    /// <summary>常驻端轮询检查跨权限唤醒文件；消费并删掉文件后返回 true。</summary>
    public static bool ConsumeShowTrigger(string baseDirectory)
    {
        string path = ShowTriggerPath(baseDirectory);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool SignalStop() => Set(StopName);

    public static bool SignalPause() => Set(PauseName);

    public static bool SignalShow() => Set(ShowName);

    public static bool SignalResume()
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(PauseName);
            handle.Reset();
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Set(string name)
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(name);
            handle.Set();
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略并发清理冲突
        }
    }
}
