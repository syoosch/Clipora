using System.Collections.Generic;
using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>剪贴板记录的持久化与查询。实现：<c>SqliteClipStore</c>。</summary>
public interface IClipStore
{
    /// <summary>
    /// 新增一条记录。<paramref name="mergeDuplicates"/> 为 true 且已存在相同
    /// <see cref="ClipItem.ContentHash"/> 的未删除记录时，刷新其时间到最前并返回其 Id。
    /// </summary>
    long Add(ClipItem item, bool mergeDuplicates);

    /// <summary>按条件查询（置顶优先、时间倒序、分页）。</summary>
    IReadOnlyList<ClipItem> Query(ClipQuery query);

    ClipItem? GetById(long id);

    void SetPinned(long id, bool pinned);

    /// <summary>软删除：移入回收站。</summary>
    void SoftDelete(long id);

    /// <summary>从回收站恢复。</summary>
    void Restore(long id);

    /// <summary>彻底删除（同时应由上层清理其副本/缩略图文件）。</summary>
    void HardDelete(long id);

    /// <summary>清理过期项（跳过置顶）。0 天表示永久不清理。返回被删除的项，供上层清磁盘文件。</summary>
    IReadOnlyList<ClipItem> PurgeExpired(int retentionDays);

    /// <summary>清空回收站中保留超过指定天数的项。返回被删除的项。</summary>
    IReadOnlyList<ClipItem> PurgeRecycleBin(int keepDays);

    /// <summary>清空列表（可选保留置顶）。</summary>
    void Clear(bool keepPinned);

    // —— M5.1 OCR ——

    /// <summary>回写单条记录的 OCR 结果。</summary>
    void SetOcrResult(long id, OcrOutcome outcome, string? text);

    /// <summary>按优先级取待 OCR 的图片（最多 <paramref name="take"/> 条）。</summary>
    IReadOnlyList<ClipItem> ListPendingOcr(int take);

    /// <summary>将历史图片（Type=Image 且 OcrStatus=None）批量标记为 Pending，供一次性回填。</summary>
    void MarkLegacyImagesPending();
}
