using System;
using System.Collections.Generic;
using Clipora.Models;
using Clipora.Services;

namespace Clipora.ViewModels;

/// <summary>
/// 时间段分组策略 + 折叠态缓存（行为保持型重构：原先散落在 MainViewModel / TimeFormat /
/// ClipGroupHeader / HistoryWindow 四处的「8 小时段 + 置顶组恒展开 + 默认只展开最新段 +
/// 捕获新条目展开其所在段 + 点击翻转」收口于此）。
///
/// 只负责"段键策略 + header 缓存 + 展开决策"，不触碰 <see cref="System.Windows.Data.ICollectionView"/>
/// 与 <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>；折叠态翻转的是缓存中
/// 同一个 <see cref="ClipGroupHeader"/> 实例，故跨 Refresh 保持、保住 WPF 原生增量与虚拟化。
/// </summary>
public sealed class ClipGrouping
{
    /// <summary>置顶组段键。</summary>
    public const string PinnedKey = "置顶";

    private readonly Dictionary<string, ClipGroupHeader> _groups = new();

    /// <summary>段键策略（纯函数）：置顶项归入"置顶"组，其余按"绝对日期 + 8 小时段"。
    /// 键按真实日期绝对化，跨零点时次日同段不会与前一天同段冲突。</summary>
    public static string KeyFor(ClipItem item) =>
        item.IsPinned ? PinnedKey : TimeFormat.SegmentKey(item.CreatedAt);

    /// <summary>解析条目所属分组：同一段缓存同一 <see cref="ClipGroupHeader"/> 实例（保持折叠态）。</summary>
    public ClipGroupHeader Resolve(ClipItem item)
    {
        string key = KeyFor(item);
        if (!_groups.TryGetValue(key, out ClipGroupHeader? group))
        {
            group = new ClipGroupHeader(key, item.CreatedAt, isPinned: item.IsPinned);
            _groups[key] = group;
        }
        return group;
    }

    /// <summary>清空 header 缓存（整表重载前调用）。</summary>
    public void Clear() => _groups.Clear();

    /// <summary>跨零点刷新所有分组标题（今天→昨天等）。段键不变、条目不重分组。</summary>
    public void NotifyDateChanged()
    {
        foreach (ClipGroupHeader group in _groups.Values)
            group.NotifyDateChanged();
    }

    /// <summary>默认仅展开最新一段（非置顶）；置顶组恒展开。须在条目已通过 <see cref="Resolve"/> 预填充缓存后调用。</summary>
    public void ApplyDefaultExpansion(IEnumerable<ClipItem> items)
    {
        string? newestKey = null;
        DateTime newest = DateTime.MinValue;
        foreach (ClipItem item in items)
        {
            if (item.IsPinned)
                continue;
            DateTime localCreated = item.CreatedAt.ToLocalTime();
            if (localCreated > newest)
            {
                newest = localCreated;
                newestKey = TimeFormat.SegmentKey(item.CreatedAt);
            }
        }

        foreach ((string key, ClipGroupHeader group) in _groups)
        {
            if (group.IsPinned)
                continue;
            group.IsExpanded = key == newestKey;
        }
    }

    /// <summary>展开某条目所属分组（新捕获条目落入折叠组时调用，避免"看不见"）。</summary>
    public void ExpandFor(ClipItem item) => Resolve(item).IsExpanded = true;

    /// <summary>点击分组标题：翻转展开态（置顶组忽略），不触碰集合。</summary>
    public void Toggle(ClipGroupHeader group)
    {
        if (group.IsPinned)
            return;
        group.IsExpanded = !group.IsExpanded;
    }
}
