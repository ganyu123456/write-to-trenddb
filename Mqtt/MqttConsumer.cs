using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
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

    // MQTT 收包回调 → 后台消费循环 之间的有界队列。
    // 满时 DropOldest：淘汰最旧、保留最新，保证收包线程永不阻塞（避免 broker 侧 send_pend 堆积）。
    private readonly Channel<MqttRawMessage> _messageChannel;
    private readonly int _channelCapacity;

    private IMqttClient? _mqttClient;
    private bool _disposed;

    // 标记是否已至少成功连接过一次；
    // DisconnectedAsync 只在成功连接后才触发重连，避免与初次连接重试竞争
    private volatile bool _hasConnectedOnce;

    // 运行期累计统计，供 Worker 每分钟汇总，替代逐测点 Debug 日志
    private long _receivedMessageCount;
    private long _receivedPointCount;
    private long _nullSkippedCount;
    private long _disconnectCount;
    private long _droppedMessageCount;

    // 重连互斥标记：0=空闲，1=已有重连任务在跑，避免并发 ConnectWithRetryAsync
    private int _reconnecting;

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

        // 容量来自配置（Mqtt.ChannelCapacity），至少为 1。单位是「消息条数」，不是测点数。
        _channelCapacity = Math.Max(1, _mqttSettings.ChannelCapacity);
        _messageChannel = Channel.CreateBounded<MqttRawMessage>(new BoundedChannelOptions(_channelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

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
            _logger.LogInformation("MQTT 已连接（ClientId={ClientId}）：{Broker}:{Port}", _mqttSettings.ClientId, _mqttSettings.Broker, _mqttSettings.Port);
            return Task.CompletedTask;
        };

        // DisconnectedAsync 只负责"曾经成功连接后断线"的重连；
        // 初次连接失败由 ConnectWithRetryAsync 的 while 循环独立处理，两条路径互不干扰。
        _mqttClient.DisconnectedAsync += args =>
        {
            Interlocked.Increment(ref _disconnectCount);
            _logger.LogWarning("MQTT 断开连接（ClientId={ClientId}）：{Reason}", _mqttSettings.ClientId, args.ReasonString);

            if (_hasConnectedOnce && !ct.IsCancellationRequested
                && Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0)
            {
                _logger.LogInformation("MQTT 断线重连：5 秒后开始重连（ClientId={ClientId}）", _mqttSettings.ClientId);

                // 用 CancellationToken.None 启动任务，让任务本身用 ct 感知关闭
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        await ConnectWithRetryAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* 关闭时忽略 */ }
                    finally
                    {
                        Interlocked.Exchange(ref _reconnecting, 0);
                    }
                }, CancellationToken.None);
            }

            return Task.CompletedTask;
        };

        // 后台消费循环：从 Channel 取原始消息做反序列化 + 映射，与收包线程解耦
        _ = Task.Run(() => ConsumeLoopAsync(ct), CancellationToken.None);

        await ConnectWithRetryAsync(ct).ConfigureAwait(false);
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 若已连接（并发重连中对方已连上，或连接成功后订阅失败再重试），
                // 跳过 ConnectAsync 直接补订阅，避免重复连接抛「already connected」异常
                if (!_mqttClient!.IsConnected)
                {
                    var optionsBuilder = new MqttClientOptionsBuilder()
                        .WithTcpServer(_mqttSettings.Broker, _mqttSettings.Port)
                        .WithClientId(_mqttSettings.ClientId)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                        .WithCleanSession(true);

                    if (!string.IsNullOrEmpty(_mqttSettings.Username))
                        optionsBuilder = optionsBuilder.WithCredentials(_mqttSettings.Username, _mqttSettings.Password);

                    var connectResult = await _mqttClient
                        .ConnectAsync(optionsBuilder.Build(), ct)
                        .ConfigureAwait(false);

                    if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                    {
                        _logger.LogWarning("MQTT 连接被拒绝：{Code}，5 秒后重试", connectResult.ResultCode);
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                // 订阅所有配置的主题（重复订阅幂等）
                foreach (var topic in _mqttSettings.Topics)
                {
                    var subOptions = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build();
                    await _mqttClient.SubscribeAsync(subOptions, ct).ConfigureAwait(false);
                    _logger.LogInformation("已订阅主题：{Topic}（QoS=AtLeastOnce）", topic);
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
        // 收包回调只做一件事：拷贝 payload 并入队，立即返回，不阻塞 MQTTnet 收包循环。
        // 反序列化 + 映射等重活交给 ConsumeLoopAsync。
        try
        {
            var payload = args.ApplicationMessage.PayloadSegment.ToArray();
            var topic = args.ApplicationMessage.Topic;

            // DropOldest 满时 TryWrite 仍返回 true（淘汰最旧后写入），无法靠返回值判断丢弃；
            // 因此在写入前用 Count 预检查：已满则本次会淘汰最旧一条，计入丢弃指标。
            if (_messageChannel.Reader.Count >= _channelCapacity)
            {
                Interlocked.Increment(ref _droppedMessageCount);
            }

            if (!_messageChannel.Writer.TryWrite(new MqttRawMessage(topic, payload)))
            {
                // 仅在 Channel 已关闭时才走到这里
                Interlocked.Increment(ref _droppedMessageCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "入队 MQTT 消息时发生异常，主题：{Topic}", args.ApplicationMessage.Topic);
        }

        return Task.CompletedTask;
    }

    /// <summary>后台消费循环：从 Channel 取出原始消息，做解码、反序列化、映射过滤，写入缓冲区。</summary>
    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var raw in _messageChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ProcessMessage(raw.Topic, raw.Payload);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 关闭时正常退出
        }
    }

    private void ProcessMessage(string topic, byte[] payload)
    {
        try
        {
            var json = Encoding.UTF8.GetString(payload);

            // 消息格式：{"timestamp":<ms>,"deviceId":"...","batchData":{"测点名":{"value":...,"timestamp":<s>,"state":...},...}}
            var msg = JsonSerializer.Deserialize<MqttBatchMessage>(json);
            if (msg?.BatchData is null || msg.BatchData.Count == 0)
            {
                _logger.LogDebug("主题 {Topic} 收到空消息或 batchData 为空，已跳过", topic);
                return;
            }

            var matched = 0;
            foreach (var (name, sv) in msg.BatchData)
            {
                if (!_nameMapping.TryGetValue(name, out var targetName))
                    continue;

                // 采集端对「无数据」测点可能下发 JSON null，跳过而非让整批中断
                if (sv is null)
                {
                    Interlocked.Increment(ref _nullSkippedCount);
                    continue;
                }

                _buffer[targetName] = new TagData
                {
                    Value      = sv.Value,
                    TimeStamp  = ParseUnixTimestamp(sv.Timestamp),
                    ValueState = sv.State
                };
                matched++;
            }

            Interlocked.Increment(ref _receivedMessageCount);
            Interlocked.Add(ref _receivedPointCount, matched);

            var sendTime = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp).ToLocalTime();
            _logger.LogInformation(
                "主题 {Topic}（设备 {DeviceId}）：收到 {Total} 条，命中映射 {Matched} 条，缓冲区 {BufSize}，消息发送时间 {SendTime:yyyy-MM-dd HH:mm:ss.fff}",
                topic, msg.DeviceId, msg.BatchData.Count, matched, _buffer.Count, sendTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 MQTT 消息时发生异常，主题：{Topic}", topic);
        }
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

    /// <summary>获取运行期累计统计，供 Worker 定时汇总。</summary>
    public MqttStats GetStats() => new(
        Interlocked.Read(ref _receivedMessageCount),
        Interlocked.Read(ref _receivedPointCount),
        Interlocked.Read(ref _nullSkippedCount),
        Interlocked.Read(ref _disconnectCount),
        Interlocked.Read(ref _droppedMessageCount));

    /// <summary>将 Unix 秒级时间戳转换为 UTC DateTime。</summary>
    private static DateTime ParseUnixTimestamp(long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _messageChannel.Writer.TryComplete();

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

/// <summary>MQTT 消费端运行期累计统计。</summary>
public sealed record MqttStats(
    long ReceivedMessages,
    long ReceivedPoints,
    long NullSkipped,
    long Disconnects,
    long DroppedMessages);

/// <summary>入队用的原始 MQTT 消息（已拷贝 payload，避免收包缓冲区被复用）。</summary>
public sealed record MqttRawMessage(string Topic, byte[] Payload);
