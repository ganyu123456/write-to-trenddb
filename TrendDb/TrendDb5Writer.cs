using Ld.COMMON;
using WriteToTrendDb.Models;

namespace WriteToTrendDb.TrendDb;

/// <summary>
/// TrendDB5 实时值写入实现，核心方法对应原项目的 SetRtValuesByNames。
/// 流程：
///   1. 按 db 前缀将测点分组（db01.tag1 → dbName=db01, tagName=tag1）
///   2. 逐 db 构建 TagValue 列表
///   3. 调用 Pool.SetValueByTagName 批量写入
/// </summary>
public sealed class TrendDb5Writer : ITrendDb5Writer
{
    private readonly TrendDb5ConnectionPool _pool;
    private readonly ILogger<TrendDb5Writer> _logger;

    public TrendDb5Writer(TrendDb5ConnectionPool pool, ILogger<TrendDb5Writer> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    public bool SetRtValuesByNames(IDictionary<string, TagData> input)
    {
        if (input.Count == 0) return true;

        _logger.LogDebug("SetRtValuesByNames 入参：{Count} 个测点", input.Count);

        var pool = _pool.Acquire();
        if (pool is null)
        {
            _logger.LogWarning("TrendDB5 连接池不可用，跳过本次写入");
            return false;
        }

        // 按数据库名分组：db01.tag1 → ["db01" → ["tag1", ...]]
        var tagsByDb = GroupByDatabase(input.Keys.ToList());
        var allSuccess = true;

        foreach (var (dbName, shortNames) in tagsByDb)
        {
            if (string.IsNullOrEmpty(dbName))
            {
                _logger.LogWarning("测点名缺少数据库前缀，跳过：{Tags}", string.Join(", ", shortNames));
                allSuccess = false;
                continue;
            }

            try
            {
                var tagValues = new List<TagValue>();
                var validShortNames = new List<string>();

                foreach (var shortName in shortNames)
                {
                    var fullName = dbName + "." + shortName;
                    if (!input.TryGetValue(fullName, out var td)) continue;

                    var tv = new TagValue();
                    // SetValue(value, timestampMs, valueState)
                    // valueState: 1=Good, 0=Unknown/Bad（与原项目 ToValueStateOrgin() 保持一致）
                    var tsMs = ToUnixMilliseconds(td.TimeStamp);
                    uint stateCode = td.ValueState == 1 ? 1u : 0u;
                    tv.SetValue(td.Value, tsMs, stateCode);

                    tagValues.Add(tv);
                    validShortNames.Add(shortName);
                }

                if (validShortNames.Count == 0) continue;

                var resList = new List<int>();
                var ret = pool.SetValueByTagName(dbName, validShortNames, tagValues, ref resList);

                if (!ret.Ok())
                {
                    _logger.LogWarning(
                        "SetValueByTagName 调用失败：db={Db}, retCode={Ret}, sysCode={Sys}",
                        dbName, ret.retCode, ret.sysCode);
                    allSuccess = false;
                }
                else
                {
                    // 检查逐测点写入结果
                    for (var i = 0; i < resList.Count && i < validShortNames.Count; i++)
                    {
                        if (resList[i] < 0)
                        {
                            _logger.LogWarning(
                                "测点写入失败：db={Db}, tag={Tag}, code={Code}",
                                dbName, validShortNames[i], resList[i]);
                        }
                    }

                    _logger.LogDebug("成功写入 {Count} 个测点到 {Db}", validShortNames.Count, dbName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入 TrendDB5 数据库 {Db} 时发生异常", dbName);
                allSuccess = false;
            }
        }

        return allSuccess;
    }

    /// <summary>
    /// 将完整测点名列表按数据库名分组。
    /// "db01.tag1" → dbName="db01", shortName="tag1"
    /// "db01.group.tag1" → dbName="db01", shortName="group.tag1"（保留多级名称）
    /// 无"."的测点 → dbName=""（会被跳过并记录警告）
    /// </summary>
    private static Dictionary<string, List<string>> GroupByDatabase(IList<string> fullNames)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fullName in fullNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dotIdx = fullName.IndexOf('.');
            var db = dotIdx > 0 ? fullName[..dotIdx] : string.Empty;
            var shortName = dotIdx > 0 ? fullName[(dotIdx + 1)..] : fullName;

            if (!result.TryGetValue(db, out var list))
            {
                list = [];
                result[db] = list;
            }
            list.Add(shortName);
        }

        return result;
    }

    /// <summary>将 DateTime 转换为 TrendDB5 要求的毫秒级 Unix 时间戳（ulong）。</summary>
    private static ulong ToUnixMilliseconds(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc) // Unspecified 按 UTC 处理
        };
        return (ulong)new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }
}
