using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeyTrace.Collect;

/// <summary>
/// 事件字段与 <c>docs/event-schema.md</c> 一一对应。
/// 这里手写序列化而不是用反射版 JsonSerializer，有两个原因：
/// 1. 采集是热路径，反射每次都要建元数据，没必要
/// 2. 字段顺序固定，日志文件人眼好读
/// </summary>
internal sealed class KeyEvent
{
    /// <summary>中文字段不能被转义成 \uXXXX，否则日志没法直接看。</summary>
    private static readonly JsonSerializerOptions Quoting = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public long Ts { get; init; }
    public long Mono { get; init; }
    public string Ev { get; init; } = "kd";
    public int Vk { get; init; }
    public int Sc { get; init; }
    public int Mods { get; init; }
    public int Pid { get; init; }
    public string Exe { get; init; } = "unknown";
    public string Sid { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? Src { get; init; }

    /// <summary>
    /// 促成这次上屏的击键数。0 表示文本不是打出来的（粘贴、拖拽、语音），
    /// 归因层据此把这些 commit 排除在"打错"统计之外。
    /// </summary>
    public int? Keys { get; init; }

    public string ToJsonLine()
    {
        StringBuilder sb = new(192);
        sb.Append("{\"v\":1");
        sb.Append(",\"ts\":").Append(Ts.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"mono\":").Append(Mono.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"ev\":").Append(Quote(Ev));

        if (Ev is "kd" or "ku")
        {
            sb.Append(",\"vk\":").Append(Vk.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"sc\":").Append(Sc.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"mods\":").Append(Mods.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append(",\"pid\":").Append(Pid.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"exe\":").Append(Quote(Exe));
        sb.Append(",\"sid\":").Append(Quote(Sid));

        if (Text is not null)
        {
            sb.Append(",\"text\":").Append(Quote(Text));
            sb.Append(",\"src\":").Append(Quote(Src ?? "uia"));

            if (Keys is not null)
            {
                sb.Append(",\"keys\":").Append(Keys.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, Quoting);
}
