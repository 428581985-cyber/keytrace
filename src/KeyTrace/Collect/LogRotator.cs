using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace KeyTrace.Collect;

/// <summary>
/// 日志轮转：把过期的按天文件压成 .gz，再把更久远的删掉。
///
/// 不做这件事的后果很直接：事件是逐键写的，一天几十万行，一个月后 report 读一遍要几十秒，
/// 而且没人会记得手动清理。
///
/// 只处理"已经过完的日期"，当天正在写的文件不碰。
/// </summary>
internal static class LogRotator
{
    /// <summary>过完一天就压。</summary>
    private static readonly TimeSpan CompressAfter = TimeSpan.FromDays(1);

    /// <summary>压缩包保留 30 天。</summary>
    private static readonly TimeSpan DeleteAfter = TimeSpan.FromDays(30);

    public static void Run(string directory, DateOnly today)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            CompressOldFiles(directory, today);
            DeleteExpiredArchives(directory, today);
        }
        catch (IOException)
        {
            // 轮转失败不该影响采集
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CompressOldFiles(string directory, DateOnly today)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            if (!TryParseDate(Path.GetFileNameWithoutExtension(path), out DateOnly day))
            {
                continue;
            }

            // 当天和昨天的文件保持原样，正在写或刚写完
            if (today.DayNumber - day.DayNumber < CompressAfter.TotalDays)
            {
                continue;
            }

            string target = path + ".gz";
            using (FileStream input = File.OpenRead(path))
            using (FileStream output = File.Create(target))
            using (GZipStream gzip = new(output, CompressionLevel.SmallestSize))
            {
                input.CopyTo(gzip);
            }

            File.Delete(path);
        }
    }

    private static void DeleteExpiredArchives(string directory, DateOnly today)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl.gz"))
        {
            string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            if (!TryParseDate(name, out DateOnly day))
            {
                continue;
            }

            if (today.DayNumber - day.DayNumber > DeleteAfter.TotalDays)
            {
                File.Delete(path);
            }
        }
    }

    private static bool TryParseDate(string stem, out DateOnly day) =>
        DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}
