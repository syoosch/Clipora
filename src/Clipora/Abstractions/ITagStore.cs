using System.Collections.Generic;
using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>标签的管理与关联。打标签只能用已有标签。实现：<c>SqliteTagStore</c>。</summary>
public interface ITagStore
{
    IReadOnlyList<Tag> List();

    long Create(string name, string color);

    void Rename(long id, string name);

    void SetColor(long id, string color);

    void Reorder(long id, int sortOrder);

    void Delete(long id);

    /// <summary>某条记录已打的标签 Id 列表。</summary>
    IReadOnlyList<long> GetTagIds(long clipId);

    /// <summary>一次性读取全部「记录 Id → 标签 Id 列表」关联，供列表全量加载时避免 N+1 查询。</summary>
    IReadOnlyDictionary<long, IReadOnlyList<long>> GetAllTagAssignments();

    void Assign(long clipId, long tagId);

    void Unassign(long clipId, long tagId);
}
