using System.Text;
using Serilog.Events;
using Serilog.Formatting;

namespace WriteToTrendDb.Configuration;

/// <summary>
/// 平台日志规范的 JSON formatter。
/// 输出字段：time / level（大写 TRACE|DEBUG|INFO|WARN|ERROR|FATAL）/ message / service。
/// 对齐云边协同平台 Fluent Bit 的 json_log Parser 提取规则，
/// 使日志级别能被日志中心正确识别并分流。
/// </summary>
public sealed class PlatformJsonFormatter : ITextFormatter
{
    /// <inheritdoc/>
    public void Format(LogEvent logEvent, TextWriter output)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "TRACE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARN",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "INFO",
        };

        var sb = new StringBuilder(256);
        sb.Append("{\"time\":\"");
        sb.Append(logEvent.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
        sb.Append("\",\"level\":\"");
        sb.Append(level);
        sb.Append("\",\"message\":\"");
        sb.Append(JsonEscape(logEvent.RenderMessage()));
        sb.Append("\",\"service\":\"write-to-trenddb\"");

        if (logEvent.Exception != null)
        {
            sb.Append(",\"exception\":\"");
            sb.Append(JsonEscape(logEvent.Exception.ToString()));
            sb.Append('"');
        }

        sb.Append('}');
        output.WriteLine(sb.ToString());
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
