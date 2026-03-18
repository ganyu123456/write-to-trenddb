using System.Text.Json.Serialization;

namespace WriteToTrendDb.Models;

/// <summary>
/// MQTT 消息中单条传感器数据，与 sensor-simulator-mapper 推送的 JSON 数组元素对应。
/// 推送格式示例：
///   [{"name":"db01.tag1","value":123.45,"timestamp":"2026-03-15T08:00:00Z","value_state":1}, ...]
/// </summary>
public sealed record SensorMessage(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("value")]       double Value,
    [property: JsonPropertyName("timestamp")]   string Timestamp,
    [property: JsonPropertyName("value_state")] int ValueState
);

/// <summary>
/// 内部使用的测点数据结构，与 TrendDB5 写入接口对应。
/// </summary>
public sealed class TagData
{
    public double Value { get; set; }
    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;

    /// <summary>值状态：1 = Good（正常），0 = Unknown/Bad（异常）。</summary>
    public int ValueState { get; set; } = 1;
}
