using System.Text.Json.Serialization;

namespace WriteToTrendDb.Models;

/// <summary>
/// MQTT 消息外层包装结构。
/// 实际消息格式（主题 device/sis/data）：
/// {
///   "timestamp": 1780076839551,         ← 消息发送时间，Unix 毫秒
///   "deviceId":  "sis-collect-dev-dy",
///   "batchData": {
///     "DDM.SIS.1DCS_10DCS_TDM_01": {"value":0,"timestamp":1600397002,"state":1},
///     ...
///   }
/// }
/// </summary>
public sealed record MqttBatchMessage(
    [property: JsonPropertyName("timestamp")] long                             Timestamp,
    [property: JsonPropertyName("deviceId")]  string                           DeviceId,
    [property: JsonPropertyName("batchData")] Dictionary<string, SensorValue>  BatchData
);

/// <summary>
/// batchData 中单个测点的值对象，timestamp 为每条测点数据的采集时间，Unix 秒级。
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
