using System;
using System.Threading;

namespace KeyTrace.Control;

/// <summary>
/// 跨进程控制通道。刻意不用命名管道：这里要传的只有三个开关，
/// 而 Windows 的命名事件本身就是"有信号 / 无信号"的布尔状态，
/// 天然对应"暂停 / 继续"，比定义一套命令协议简单得多。
///
/// 命名用 <c>Local\</c> 而不是 <c>Global\</c>：只在当前登录会话内可见，
/// 不需要额外权限，也不会被同机器其他用户干扰。
/// </summary>
internal static class ControlSignals
{
    private const string StopName = @"Local\KeyTrace.Stop";
    private const string PauseName = @"Local\KeyTrace.Pause";
    private const string InstanceName = @"Local\KeyTrace.Instance";

    /// <summary>常驻端创建三个内核对象。重复创建同名对象会拿到已存在的那个，所以要先 Reset 清掉上次残留的状态。</summary>
    public static (EventWaitHandle Stop, EventWaitHandle Pause) CreateServerSide()
    {
        // initialState 传 true 再 Reset，是为了避免"第一次创建"和"打开已存在"两种路径行为不一致
        EventWaitHandle stop = new(true, EventResetMode.ManualReset, StopName);
        stop.Reset();

        EventWaitHandle pause = new(true, EventResetMode.ManualReset, PauseName);
        pause.Reset();

        return (stop, pause);
    }

    /// <summary>返回 null 表示已经有一个实例在跑。</summary>
    public static Mutex? TryAcquireInstance()
    {
        Mutex mutex = new(initiallyOwned: true, InstanceName, out bool createdNew);
        if (createdNew)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    public static bool SignalStop() => Set(StopName);

    public static bool SignalPause() => Set(PauseName);

    public static bool SignalResume()
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(PauseName);
            handle.Reset();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
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
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
