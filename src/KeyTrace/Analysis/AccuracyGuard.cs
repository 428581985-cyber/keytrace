namespace KeyTrace.Analysis;

internal sealed record AccuracyErrorKey(int Vk, string Name, int Typos, int Presses, double TypoRate);

internal sealed record AccuracyReport(
    double AccuracyPercent,
    double ThresholdPercent,
    bool Triggered,
    IReadOnlyList<AccuracyErrorKey> TopErrorKeys);

/// <summary>
/// 准确率护栏：真实打字准确率跌破阈值（默认 95%）时触发提醒并列出错误最多的键，
/// 避免在准确率已经走低的情况下继续盲目追速度。
/// </summary>
internal static class AccuracyGuard
{
    public const double DefaultThresholdPercent = 95.0;

    public static AccuracyReport Evaluate(AttributionResult result, double thresholdPercent = DefaultThresholdPercent)
    {
        int keystrokes = result.Totals.Keystrokes;
        int typos = result.Totals.Typos;
        double accuracy = keystrokes > 0
            ? Math.Clamp((1.0 - (double)typos / keystrokes) * 100.0, 0.0, 100.0)
            : 100.0;

        bool triggered = keystrokes > 0 && typos > 0 && accuracy < thresholdPercent;

        List<AccuracyErrorKey> top = [.. result.Keys.Values
            .Where(k => k.Typos > 0)
            .Select(k => new AccuracyErrorKey(
                k.Vk,
                KeyCatalog.Name(k.Vk),
                k.Typos,
                k.Presses,
                k.Presses > 0 ? (double)k.Typos / k.Presses : 0))
            .OrderByDescending(k => k.Typos)
            .ThenByDescending(k => k.TypoRate)
            .ThenBy(k => k.Vk)
            .Take(5)];

        return new AccuracyReport(accuracy, thresholdPercent, triggered, top);
    }
}
