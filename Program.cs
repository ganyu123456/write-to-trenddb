using WriteToTrendDb.Configuration;
using WriteToTrendDb.Mqtt;
using WriteToTrendDb.TrendDb;
using WriteToTrendDb.Workers;

var builder = Host.CreateApplicationBuilder(args);

// 若配置了 PointsFilePath，则从 CSV 文件加载点表并注入为 TagMappings 配置，
// 覆盖 appsettings.json 中的静态 TagMappings（CSV 优先级更高）。
var pointsFilePath = builder.Configuration["PointsFilePath"];
if (!string.IsNullOrWhiteSpace(pointsFilePath))
{
    if (!File.Exists(pointsFilePath))
    {
        // 启动即失败，避免服务静默丢点
        throw new FileNotFoundException($"点表文件未找到，请确认 hostPath 挂载正确：{pointsFilePath}");
    }

    var entries = new List<KeyValuePair<string, string?>>();
    var idx = 0;
    foreach (var line in File.ReadLines(pointsFilePath))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed)) continue;

        var comma = trimmed.IndexOf(',');
        if (comma <= 0) continue; // 跳过格式错误行

        var source = trimmed[..comma].Trim();
        var target = trimmed[(comma + 1)..].Trim();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) continue;

        entries.Add(new($"TagMappings:{idx}:Source", source));
        entries.Add(new($"TagMappings:{idx}:Target", target));
        idx++;
    }

    builder.Configuration.AddInMemoryCollection(entries);

    var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup");
    logger.LogInformation("已从点表文件加载 {Count} 条映射：{Path}", idx, pointsFilePath);
}

// Bind the entire config root to AppSettings
builder.Services.Configure<AppSettings>(builder.Configuration);

// TrendDB5 connection pool – singleton, holds Pool objects across the app lifetime
builder.Services.AddSingleton<TrendDb5ConnectionPool>();

// TrendDB5 writer depends on the pool
builder.Services.AddSingleton<ITrendDb5Writer, TrendDb5Writer>();

// MQTT consumer – singleton message buffer
builder.Services.AddSingleton<MqttConsumer>();

// Background worker: wakes on a timer, flushes buffer → writes to TrendDB5
builder.Services.AddHostedService<TrendDbWriteWorker>();

var host = builder.Build();
await host.RunAsync();
