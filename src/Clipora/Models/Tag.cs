namespace Clipora.Models;

/// <summary>
/// 用户标签。打标签时只能从已有标签中选择（保证标签筛选清晰）；
/// 新标签通过筛选区 "+" 或设置统一创建。
/// </summary>
public sealed class Tag
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>柔和颜色（#RRGGBB），默认强调蓝，可在设置中调整。</summary>
    public string Color { get; set; } = "#0078D4";

    public int SortOrder { get; set; }
}
