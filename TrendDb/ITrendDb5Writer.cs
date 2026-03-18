using WriteToTrendDb.Models;

namespace WriteToTrendDb.TrendDb;

/// <summary>
/// TrendDB5 实时值写入接口，方便后续单元测试时替换为 Mock 实现。
/// </summary>
public interface ITrendDb5Writer
{
    /// <summary>
    /// 批量回写实时值。
    /// </summary>
    /// <param name="input">Key = 完整测点名（含数据库前缀，例如 db01.tag1），Value = 测点数据。</param>
    /// <returns>全部写入成功返回 true，有任意失败返回 false。</returns>
    bool SetRtValuesByNames(IDictionary<string, TagData> input);
}
