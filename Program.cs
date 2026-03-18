using WriteToTrendDb.Configuration;
using WriteToTrendDb.Mqtt;
using WriteToTrendDb.TrendDb;
using WriteToTrendDb.Workers;

var builder = Host.CreateApplicationBuilder(args);

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
