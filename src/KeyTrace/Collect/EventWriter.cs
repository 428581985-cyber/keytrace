using System;
using System.IO;
using System.Text;

namespace KeyTrace.Collect;

/// <summary>
/// 追加写 JSONL，按天分文件。
/// 不逐条 flush：每按一个键就落盘会白白打断采集线程，而这点数据丢了也不心疼。
/// 但退出时必须 flush，否则最后几十条会丢。
/// </summary>
internal sealed class EventWriter : IDisposable
{
    private const int FlushEvery = 32;

    private readonly string _directory;

    /// <summary>
    /// 两个线程会同时写：消息循环线程写击键，UIA 采样线程写上屏文本。
    /// StreamWriter 不是线程安全的，所以所有读写都串行化。
    /// </summary>
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private DateOnly _day;
    private int _pending;
    private bool _disposed;

    public EventWriter(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        LogRotator.Run(directory, DateOnly.FromDateTime(DateTime.Now));
    }

    public string OutputDirectory => _directory;

    public void Write(string line, DateOnly today)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            EnsureWriterFor(today);
            _writer!.WriteLine(line);

            if (++_pending >= FlushEvery)
            {
                Flush();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _pending = 0;
        }
    }

    private void EnsureWriterFor(DateOnly today)
    {
        if (_writer is not null && today == _day)
        {
            return;
        }

        CloseWriter();

        // 跨天时顺手轮转一次，长跑进程不用等下次重启
        LogRotator.Run(_directory, today);

        string path = Path.Combine(_directory, $"{today.ToString("yyyy-MM-dd")}.jsonl");
        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
        _day = today;
    }

    private void CloseWriter()
    {
        if (_writer is null)
        {
            return;
        }

        _writer.Flush();
        _writer.Dispose();
        _writer = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseWriter();
    }
}
