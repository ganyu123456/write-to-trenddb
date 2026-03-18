using Microsoft.Extensions.Options;
using WriteToTrendDb.Configuration;
using WriteToTrendDb.Mqtt;
using WriteToTrendDb.TrendDb;

namespace WriteToTrendDb.Workers;

/// <summary>
/// 后台写入 Worker。
/// 负责：
///   1. 启动 MQTT 消费者，订阅指定主题
///   2. 以配置的时间间隔（WriteIntervalSeconds）触发定时器
///   3. 每次定时触发时从 MqttConsumer 取出最新缓存，批量写入 TrendDB5
/// </summary>
public sealed class TrendDbWriteWorker : BackgroundService
{
    private readonly ILogger<TrendDbWriteWorker> _logger;
    private readonly MqttConsumer _mqttConsumer;
    private readonly ITrendDb5Writer _writer;
    private readonly int _intervalSeconds;

    public TrendDbWriteWorker(
        ILogger<TrendDbWriteWorker> logger,
        MqttConsumer mqttConsumer,
        ITrendDb5Writer writer,
        IOptions<AppSettings> options)
    {
        _logger = logger;
        _mqttConsumer = mqttConsumer;
        _writer = writer;
        _intervalSeconds = Math.Max(1, options.Value.TrendDb5.WriteIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TrendDbWriteWorker 启动，回写间隔：{Interval} 秒",
            _intervalSeconds);

        // 先启动 MQTT 消费者（内部有重连机制）
        await _mqttConsumer.StartAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var data = _mqttConsumer.Flush();

                if (data.Count == 0)
                {
                    _logger.LogDebug("本轮无新数据，跳过写入");
                    continue;
                }

                _logger.LogInformation("本轮回写 {Count} 个测点到 TrendDB5", data.Count);

                var success = _writer.SetRtValuesByNames(data);

                _logger.LogInformation(
                    "回写完成：{Result}，{Count} 个测点",
                    success ? "全部成功" : "部分失败",
                    data.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入周期发生异常，等待下一轮");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TrendDbWriteWorker 正在停止...");
        await _mqttConsumer.DisposeAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("TrendDbWriteWorker 已停止");
    }
}
