using System;
using Clipora.Common;
using Clipora.Services;

namespace Clipora.ViewModels;

/// <summary>
/// 可折叠时间段分组的分组键对象。<see cref="System.Windows.Data.PropertyGroupDescription"/>
/// 返回此对象，<c>CollectionViewGroup.Name</c> 即为它；折叠 = 翻转 <see cref="IsExpanded"/>
/// （绑定到分组容器的 ItemsPresenter.Visibility），不触碰底层集合，保住 WPF 原生增量与虚拟化。
/// 同一分组键在 <see cref="MainViewModel"/> 中缓存为同一实例，故折叠态跨 Refresh 保持。
/// </summary>
public sealed class ClipGroupHeader : ObservableObject, IEquatable<ClipGroupHeader>
{
    private bool _isExpanded;
    private readonly DateTime _representativeUtc;

    /// <summary>分组键（绝对、稳定）：用于分组、相等比较与缓存，如 "2026-06-23#0" 或 "置顶"。
    /// 不直接显示——显示用随日期变化的 <see cref="Title"/>。</summary>
    public string Key { get; }

    /// <summary>是否为置顶组（恒展开、不可折叠、不显示 chevron）。</summary>
    public bool IsPinned { get; }

    public ClipGroupHeader(string key, DateTime representativeUtc, bool isPinned = false)
    {
        Key = key;
        _representativeUtc = representativeUtc;
        IsPinned = isPinned;
        _isExpanded = isPinned; // 置顶组默认展开
    }

    /// <summary>标题显示（相对当前日期）：置顶组为"置顶"，其余为"今天/昨天/具体日期 + 段标签"。
    /// 跨零点后由 <see cref="NotifyDateChanged"/> 刷新（今天→昨天）。</summary>
    public string Title => IsPinned ? Key : TimeFormat.SegmentTitle(_representativeUtc);

    /// <summary>日期翻天时刷新标题绑定（段键不变、条目不重分组，仅相对标签由今天变昨天）。</summary>
    public void NotifyDateChanged() => OnPropertyChanged(nameof(Title));

    /// <summary>当前是否展开。置顶组恒为 true；普通组可折叠。</summary>
    public bool IsExpanded
    {
        get => IsPinned || _isExpanded;
        set
        {
            if (IsPinned) return; // 置顶组不可折叠
            if (Set(ref _isExpanded, value))
                OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    /// <summary>折叠/展开 chevron 图标（Segoe Fluent Icons）：展开=ChevronDown，折叠=ChevronRight。</summary>
    public string ChevronGlyph => IsExpanded ? "" : "";

    public bool Equals(ClipGroupHeader? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ClipGroupHeader);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    public override string ToString() => Key;
}
