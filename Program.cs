using WriteToTrendDb.Configuration;
using WriteToTrendDb.Mqtt;
using WriteToTrendDb.TrendDb;
using WriteToTrendDb.Workers;

var builder = Host.CreateApplicationBuilder(args);

// 校验点表文件是否存在（PointsFilePath 配置项指定路径）
// 点表的实际加载由 MqttConsumer 构造函数直接完成，不经过配置绑定系统，
// 避免十几万条 TagMappings 通过 IConfiguration 绑定时产生的性能问题。
var pointsFilePath = builder.Configuration["PointsFilePath"];
if (!string.IsNullOrWhiteSpace(pointsFilePath) && !File.Exists(pointsFilePath))
{
    throw new FileNotFoundException($"点表文件未找到，请确认 hostPath 挂载正确：{pointsFilePath}");
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
