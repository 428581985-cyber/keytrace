using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeyTrace.Collect;

/// <summary>
/// 持久化用户界面与采集设置（默认存于 <c>%USERPROFILE%\.keytrace\config.json</c>）。
/// 与事件目录分离：配置文件损坏时安全回退默认值，不阻塞采集启动。
/// </summary>
internal sealed class UserConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool ShowMiniHud { get; set; }
    public double? MiniHudLeft { get; set; }
    public double? MiniHudTop { get; set; }
    public bool HideLiveText { get; set; }
    public bool CaptureText { get; set; } = true;
    public string? ImeOverride { get; set; }
    public List<string> ExtraExclusions { get; set; } = [];
    public string? WebPracticeUrl { get; set; }

    internal bool HasWebPracticeUrl => !string.IsNullOrWhiteSpace(WebPracticeUrl);

    public static string FilePath(string baseDirectory) => Path.Combine(baseDirectory, "config.json");

    public static string BaseDirectoryFromEvents(string eventsDirectory)
    {
        string full = Path.GetFullPath(eventsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(full) ?? full;
    }

    public static UserConfig Load(string baseDirectory)
    {
        string path = FilePath(baseDirectory);
        if (!File.Exists(path))
        {
            return new UserConfig();
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            UserConfig? loaded = JsonSerializer.Deserialize<UserConfig>(json, JsonOptions);
            if (loaded is null)
            {
                return new UserConfig();
            }

            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UserConfig();
        }
    }

    public void Save(string baseDirectory)
    {
        Normalize();
        Directory.CreateDirectory(baseDirectory);

        string path = FilePath(baseDirectory);
        string tmp = Path.Combine(baseDirectory, $"config.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");
        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    public UserConfig Clone() => new()
    {
        ShowMiniHud = ShowMiniHud,
        MiniHudLeft = MiniHudLeft,
        MiniHudTop = MiniHudTop,
        HideLiveText = HideLiveText,
        CaptureText = CaptureText,
        ImeOverride = ImeOverride,
        ExtraExclusions = [.. ExtraExclusions],
        WebPracticeUrl = WebPracticeUrl,
    };

    private void Normalize()
    {
        ExtraExclusions ??= [];
        ExtraExclusions = [.. ExtraExclusions
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (string.IsNullOrWhiteSpace(WebPracticeUrl))
        {
            WebPracticeUrl = null;
        }
        else
        {
            WebPracticeUrl = WebPracticeUrl.Trim();
        }

        if (MiniHudLeft is double left && (double.IsNaN(left) || double.IsInfinity(left)))
        {
            MiniHudLeft = null;
        }

        if (MiniHudTop is double top && (double.IsNaN(top) || double.IsInfinity(top)))
        {
            MiniHudTop = null;
        }

        if (string.IsNullOrWhiteSpace(ImeOverride) || string.Equals(ImeOverride, "auto", StringComparison.OrdinalIgnoreCase))
        {
            ImeOverride = null;
        }
        else
        {
            ImeOverride = ImeOverride.Trim();
        }
    }
}
