using Microsoft.Extensions.Options;
using TrendDb_API;
using WriteToTrendDb.Configuration;

namespace WriteToTrendDb.TrendDb;

/// <summary>
/// TrendDB5 连接池。
/// 参照原 TrendDB5ConnectionsPool.cs 逻辑，创建指定数量的 Pool 对象并轮询供写入使用。
/// Pool.Add 内部会解析连接字符串并同时连接多个数据库（db01、db02 等），
/// 调用时通过 dbName 参数路由到具体数据库。
/// </summary>
public sealed class TrendDb5ConnectionPool : IDisposable
{
    private readonly ILogger<TrendDb5ConnectionPool> _logger;
    private readonly List<Pool> _pools = [];
    private int _roundRobinIndex;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsConnected => _pools.Count > 0;

    public TrendDb5ConnectionPool(
        IOptions<AppSettings> options,
        ILogger<TrendDb5ConnectionPool> logger)
    {
        _logger = logger;
        Initialize(options.Value.TrendDb5);
    }

    private void Initialize(TrendDb5Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            _logger.LogWarning("TrendDB5 连接字符串未配置，跳过连接池初始化");
            return;
        }

        // 跳过 "Type=TrendDB5;" 前缀，与原项目保持一致
        // 原始: Type=TrendDB5;SERVER=...;DATABASE=db01;...
        // 传入 Pool.Add 的: SERVER=...;DATABASE=db01;...
        var semicolonIdx = settings.ConnectionString.IndexOf(';');
        if (semicolonIdx < 0)
        {
            _logger.LogError("连接字符串格式不正确，缺少分号分隔符");
            return;
        }
        var cfgStr = settings.ConnectionString[(semicolonIdx + 1)..];

        var poolSize = Math.Max(1, settings.PoolSize);
        var successCount = 0;

        for (var i = 0; i < poolSize; i++)
        {
            try
            {
                var pool = new Pool();
                var resList = new List<int>();
                var ret = pool.Add(cfgStr, ref resList);

                if (ret.Ok())
                {
                    _pools.Add(pool);
                    successCount++;
                }
                else
                {
                    _logger.LogWarning(
                        "TrendDB5 连接池 [{Index}] 创建失败: retCode={RetCode}, sysCode={SysCode}, resList=[{Res}]",
                        i, ret.retCode, ret.sysCode, string.Join(",", resList));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrendDB5 连接池 [{Index}] 初始化异常", i);
            }
        }

        _logger.LogInformation(
            "TrendDB5 连接池初始化完成：{Success}/{Total} 个连接就绪",
            successCount, poolSize);
    }

    /// <summary>
    /// 以轮询方式获取一个可用的 Pool 对象。
    /// 返回 null 表示连接池为空（初始化失败）。
    /// </summary>
    public Pool? Acquire()
    {
        if (_pools.Count == 0) return null;

        lock (_lock)
        {
            var pool = _pools[_roundRobinIndex % _pools.Count];
            _roundRobinIndex = (_roundRobinIndex + 1) % _pools.Count;
            return pool;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pools.Clear();
    }
}
