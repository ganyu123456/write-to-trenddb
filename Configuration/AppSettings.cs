namespace WriteToTrendDb.Configuration;

/// <summary>
/// 根配置，直接映射 appsettings.json 的顶级节点。
/// 支持通过环境变量覆盖（双下划线分隔，例如：MQTT__BROKER=192.168.1.1）。
/// </summary>
public sealed class AppSettings
{
    public TrendDb5Settings TrendDb5 { get; set; } = new();
    public MqttSettings Mqtt { get; set; } = new();

    /// <summary>
    /// 点表 CSV 文件路径（容器内路径）。
    /// 每行格式：source,target（无表头）。
    /// 启动时由 Program.cs 读取并注入为 TagMappings 配置，优先于 appsettings.json 中的静态列表。
    /// </summary>
    public string PointsFilePath { get; set; } = string.Empty;

    /// <summary>
    /// 测点映射列表：Source 为 MQTT 消息中的测点名，Target 为写入 TrendDB5 的测点名。
    /// 只有在此列表中的 Source 测点才会被消费和回写。
    /// 通常由 Program.cs 从 PointsFilePath CSV 文件动态填充，无需在 appsettings.json 中手写。
    /// </summary>
    public List<TagMapping> TagMappings { get; set; } = [];
}

/// <summary>TrendDB5 连接与写入配置。</summary>
public sealed class TrendDb5Settings
{
    /// <summary>
    /// TrendDB5 连接字符串，支持多库配置（逗号分隔）。
    /// 格式：Type=TrendDB5;SERVER=ip:port;DATABASE=db;UID=user;PWD=pass,SERVER=...
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>连接池大小（Pool 对象数量），默认 7，与原项目保持一致。</summary>
    public int PoolSize { get; set; } = 7;

    /// <summary>定时回写间隔（秒），默认 5 秒。</summary>
    public int WriteIntervalSeconds { get; set; } = 5;
}

/// <summary>MQTT Broker 连接配置。</summary>
public sealed class MqttSettings
{
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;

    /// <summary>MQTT 客户端 ID，需在 Broker 上唯一。</summary>
    public string ClientId { get; set; } = "trenddb-writer";

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 要订阅的 MQTT 主题列表，支持通配符（例如 sensors/+/batch）。
    /// mapper 默认推送到 {topicPrefix}/batch。
    /// </summary>
    public List<string> Topics { get; set; } = [];
}

/// <summary>
/// 单条测点映射：MQTT 消息中的测点名 → TrendDB5 目标测点名。
/// 当两侧名称相同时 Source 与 Target 可填写同一个值。
/// </summary>
public sealed class TagMapping
{
    /// <summary>来源测点名（与 MQTT 消息中的 name 字段完全匹配，区分大小写）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>回写目标测点名（必须包含数据库前缀，例如 db01.tag1）。</summary>
    public string Target { get; set; } = string.Empty;
}
