using System.Text.Json.Serialization;

namespace WriteToTrendDb.Models;

/// <summary>
/// MQTT 消息中单个测点的值对象。
/// 推送格式示例（字典，key 为测点名）：
///   {"DDM.SIS.Tag01":{"value":1,"timestamp":1780041492,"state":1}, ...}
/// </summary>
public sealed record SensorValue(
    [property: JsonPropertyName("value")]     double Value,
    [property: JsonPropertyName("timestamp")] long   Timestamp,
    [property: JsonPropertyName("state")]     int    State
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
