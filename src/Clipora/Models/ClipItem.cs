using System;

namespace Clipora.Models;

/// <summary>
/// 一条剪贴板记录。文本类内容存 <see cref="TextContent"/>；
/// 图片/文件副本存数据目录，库内仅记 <see cref="RefPath"/> 与 <see cref="ThumbnailPath"/>。
/// </summary>
public sealed class ClipItem
{
    public long Id { get; set; }

    public ClipType Type { get; set; }

    /// <summary>列表预览文本（截断后的展示用文本）。</summary>
    public string PreviewText { get; set; } = string.Empty;

    /// <summary>文本 / 富文本 / URL / 代码 的原文（富文本可另存 HTML/RTF）。</summary>
    public string? TextContent { get; set; }

    /// <summary>图片 / 文件 副本在数据目录中的路径。</summary>
    public string? RefPath { get; set; }

    /// <summary>图片缩略图路径。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>来源应用显示名 / 进程名。</summary>
    public string? SourceApp { get; set; }

    /// <summary>来源应用图标缓存路径。</summary>
    public string? SourceIconPath { get; set; }

    /// <summary>创建时间（UTC 存储，显示时转本地）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsPinned { get; set; }

    /// <summary>内容哈希，用于去重 / 合并。</summary>
    public string ContentHash { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>是否已删除（回收站）。</summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    // —— M5.1 OCR ——

    /// <summary>图片 OCR 识别文本（可搜索）；null = 未处理。</summary>
    public string? OcrText { get; set; }

    /// <summary>OCR 处理状态（0=None, 1=Pending, 2=Completed, 3=Empty, 4=Failed, 5=Unsupported）。</summary>
    public OcrStatus OcrStatus { get; set; }
}
