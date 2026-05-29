using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using WriteToTrendDb.Configuration;
using WriteToTrendDb.Models;

namespace WriteToTrendDb.Mqtt;

/// <summary>
/// MQTT 消费者。
/// 订阅配置的主题，解析 sensor-simulator-mapper 推送的批量 JSON 数组，
/// 根据 TagMappings 过滤测点并缓存最新值，供 Worker 定时批量写入 TrendDB5。
/// </summary>
public sealed class MqttConsumer : IAsyncDisposable
{
    private readonly ILogger<MqttConsumer> _logger;
    private readonly MqttSettings _mqttSettings;

    // Source → Target 映射表（消费测点名 → 回写测点名）
    private readonly Dictionary<string, string> _nameMapping;

    // 线程安全的内存缓冲区：Target 测点名 → 最新测点数据
    // 每次 Flush 后清空，保证写入的是最新值
    private readonly ConcurrentDictionary<string, TagData> _buffer = new(StringComparer.OrdinalIgnoreCase);

    private IMqttClient? _mqttClient;
    private bool _disposed;

    // 标记是否已至少成功连接过一次；
    // DisconnectedAsync 只在成功连接后才触发重连，避免与初次连接重试竞争
    private volatile bool _hasConnectedOnce;

    public MqttConsumer(IOptions<AppSettings> options, IConfiguration config, ILogger<MqttConsumer> logger)
    {
        _logger = logger;
        _mqttSettings = options.Value.Mqtt;

        // 优先从 CSV 文件直接加载点表，绕开 IConfiguration 绑定，
        // 避免十几万条 TagMappings 通过配置系统绑定时的 O(n²) 性能问题。
        var pointsFilePath = config["PointsFilePath"];
        if (!string.IsNullOrWhiteSpace(pointsFilePath) && File.Exists(pointsFilePath))
        {
            _nameMapping = LoadMappingsFromCsv(pointsFilePath);
        }
        else
        {
            // 无 CSV 文件时，回退到 appsettings.json 中的静态 TagMappings（适合少量测点）
            _nameMapping = options.Value.TagMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.Source) && !string.IsNullOrWhiteSpace(m.Target))
                .ToDictionary(m => m.Source, m => m.Target, StringComparer.OrdinalIgnoreCase);
        }

        _logger.LogInformation(
            "MqttConsumer 初始化：{Count} 条测点映射，订阅主题：{Topics}",
            _nameMapping.Count,
            string.Join(", ", _mqttSettings.Topics));
    }

    /// <summary>
    /// 直接从 CSV 文件读取点表，构建 Source→Target 字典。
    /// 格式：每行 source,target（无表头），跳过空行和格式错误行。
    /// </summary>
    private static Dictionary<string, string> LoadMappingsFromCsv(string path)
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty) continue;

            var comma = trimmed.IndexOf(',');
            if (comma <= 0) continue;

            var source = trimmed[..comma].Trim().ToString();
            var target = trimmed[(comma + 1)..].Trim().ToString();
            if (source.Length > 0 && target.Length > 0)
                mapping[source] = target;
        }
        return mapping;
    }

    /// <summary>启动 MQTT 连接并订阅主题，非阻塞，断线自动重连。</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        _mqttClient.ConnectedAsync += _ =>
        {
            _logger.LogInformation("MQTT 已连接：{Broker}:{Port}", _mqttSettings.Broker, _mqttSettings.Port);
            return Task.CompletedTask;
        };

        // DisconnectedAsync 只负责"曾经成功连接后断线"的重连；
        // 初次连接失败由 ConnectWithRetryAsync 的 while 循环独立处理，两条路径互不干扰。
        _mqttClient.DisconnectedAsync += args =>
        {
            _logger.LogWarning("MQTT 断开连接：{Reason}", args.ReasonString);

            if (_hasConnectedOnce && !ct.IsCancellationRequested)
            {
                // 用 CancellationToken.None 启动任务，让任务本身用 ct 感知关闭
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        await ConnectWithRetryAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* 关闭时忽略 */ }
                }, CancellationToken.None);
            }

            return Task.CompletedTask;
        };

        await ConnectWithRetryAsync(ct).ConfigureAwait(false);
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(_mqttSettings.Broker, _mqttSettings.Port)
                    .WithClientId(_mqttSettings.ClientId)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .WithCleanSession(true);

                if (!string.IsNullOrEmpty(_mqttSettings.Username))
                    optionsBuilder = optionsBuilder.WithCredentials(_mqttSettings.Username, _mqttSettings.Password);

                var connectResult = await _mqttClient!
                    .ConnectAsync(optionsBuilder.Build(), ct)
                    .ConfigureAwait(false);

                if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                {
                    _logger.LogWarning("MQTT 连接被拒绝：{Code}，5 秒后重试", connectResult.ResultCode);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }

                // 订阅所有配置的主题
                foreach (var topic in _mqttSettings.Topics)
                {
                    var subOptions = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(topic))
                        .Build();
                    await _mqttClient.SubscribeAsync(subOptions, ct).ConfigureAwait(false);
                    _logger.LogInformation("已订阅主题：{Topic}", topic);
                }

                // 标记已成功连接，允许 DisconnectedAsync 触发断线重连
                _hasConnectedOnce = true;
                return; // 连接并订阅成功，退出重试循环
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT 连接失败，5 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        try
        {
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

            // 消息格式：{"timestamp":<ms>,"deviceId":"...","batchData":{"测点名":{"value":...,"timestamp":<s>,"state":...},...}}
            var msg = JsonSerializer.Deserialize<MqttBatchMessage>(payload);
            if (msg?.BatchData is null || msg.BatchData.Count == 0)
            {
                _logger.LogDebug("主题 {Topic} 收到空消息或 batchData 为空，已跳过", topic);
                return Task.CompletedTask;
            }

            var matched = 0;
            foreach (var (name, sv) in msg.BatchData)
            {
                if (!_nameMapping.TryGetValue(name, out var targetName))
                    continue;

                _buffer[targetName] = new TagData
                {
                    Value      = sv.Value,
                    TimeStamp  = ParseUnixTimestamp(sv.Timestamp),
                    ValueState = sv.State
                };
                matched++;
            }

            _logger.LogDebug(
                "主题 {Topic}（设备 {DeviceId}）：收到 {Total} 条，命中映射 {Matched} 条，缓冲区大小 {BufSize}",
                topic, msg.DeviceId, msg.BatchData.Count, matched, _buffer.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 MQTT 消息时发生异常，主题：{Topic}", topic);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 将缓冲区内的所有数据取出并清空，返回给 Worker 写入 TrendDB5。
    /// 使用 TryRemove 保证线程安全。
    /// </summary>
    public IDictionary<string, TagData> Flush()
    {
        var result = new Dictionary<string, TagData>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _buffer.Keys.ToList())
        {
            if (_buffer.TryRemove(key, out var td))
                result[key] = td;
        }
        return result;
    }

    /// <summary>将 Unix 秒级时间戳转换为 UTC DateTime。</summary>
    private static DateTime ParseUnixTimestamp(long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mqttClient is not null)
        {
            try
            {
                await _mqttClient.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MQTT 断开连接时发生异常（忽略）");
            }
            _mqttClient.Dispose();
        }
    }
}
