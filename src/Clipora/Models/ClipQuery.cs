using System;

namespace Clipora.Models;

/// <summary>列表查询条件：关键词 / 类型 / 标签 / 时间 + 分页（配合虚拟化无限下拉）。</summary>
public sealed class ClipQuery
{
    public string? Search { get; set; }

    public ClipType? Type { get; set; }

    public long? TagId { get; set; }

    public DateTime? Since { get; set; }

    public DateTime? Until { get; set; }

    /// <summary>是否包含回收站项（仅回收站视图为 true）。</summary>
    public bool IncludeDeleted { get; set; }

    public int Skip { get; set; }

    public int Take { get; set; } = 100;

    /// <summary>
    /// 是否按置顶优先排序。历史列表保持默认 true；需要纯时间顺序的内部流程可设为 false。
    /// </summary>
    public bool PrioritizePinned { get; set; } = true;
}
