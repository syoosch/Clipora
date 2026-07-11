using System;
using System.Collections.Generic;
using System.Linq;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>改名操作的结果。</summary>
public enum TagRenameOutcome
{
    /// <summary>已校验通过并写库。</summary>
    Renamed,
    /// <summary>名称为空或与他标签重复，未写库；调用方应回退显示名。</summary>
    Rejected,
    /// <summary>名称未变化，未写库。</summary>
    Unchanged,
}

/// <summary>
/// 标签管理编排（行为保持型重构）：把改名去重、上/下移重排序、改色无变化判定等纯逻辑
/// 从 <see cref="ViewModels.MainViewModel"/> 收口于此，仅依赖 <see cref="ITagStore"/>，不触碰 ViewModel / WPF。
/// 每个方法返回结果对象，由调用方据此刷新视图（"VM 订阅结果刷新视图"）。
/// </summary>
public sealed class TagManagement
{
    private readonly ITagStore _tagStore;

    public TagManagement(ITagStore tagStore) => _tagStore = tagStore;

    /// <summary>
    /// 校验并改名。<paramref name="newName"/> 去首尾空白后：空→Rejected；与他标签同名（忽略大小写）→Rejected；
    /// 与原名相同（区分大小写）→Unchanged；否则写库并返回 Renamed。
    /// </summary>
    public TagRenameOutcome Rename(long id, string originalName, string newName)
    {
        string name = newName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return TagRenameOutcome.Rejected;

        bool isDuplicate = _tagStore.List().Any(t =>
            t.Id != id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
            return TagRenameOutcome.Rejected;

        if (string.Equals(originalName, name, StringComparison.Ordinal))
            return TagRenameOutcome.Unchanged;

        _tagStore.Rename(id, name);
        return TagRenameOutcome.Renamed;
    }

    /// <summary>删除标签（写库）。是否联动过滤态由调用方处理。</summary>
    public void Delete(long id) => _tagStore.Delete(id);

    /// <summary>
    /// 上移（direction=-1）/下移（direction=+1）。越界返回 false（不写库）；
    /// 否则按新顺序重写全部 SortOrder（1..n）并返回 true。
    /// </summary>
    public bool Move(long id, int direction)
    {
        List<Tag> tags = _tagStore.List().ToList();
        int index = tags.FindIndex(t => t.Id == id);
        int targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= tags.Count)
            return false;

        Tag moved = tags[index];
        tags.RemoveAt(index);
        tags.Insert(targetIndex, moved);
        for (int i = 0; i < tags.Count; i++)
            _tagStore.Reorder(tags[i].Id, i + 1);

        return true;
    }

    /// <summary>改色。与当前颜色相同（忽略大小写）→ false（不写库）；否则写库并返回 true。</summary>
    public bool SetColor(long id, string currentColor, string color)
    {
        if (string.Equals(currentColor, color, StringComparison.OrdinalIgnoreCase))
            return false;

        _tagStore.SetColor(id, color);
        return true;
    }
}
