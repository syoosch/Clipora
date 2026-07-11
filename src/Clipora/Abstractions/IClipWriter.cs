using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>写回剪贴板的结果。</summary>
public enum ClipWriteResult
{
    /// <summary>已成功写回剪贴板。</summary>
    Completed,
    /// <summary>仅引用项的原文件/目录已不可用，未写入任何内容。</summary>
    ReferenceUnavailable,
}

/// <summary>把记录写回剪贴板（"再次使用"）。自动粘贴在 Phase 4.3 加入。</summary>
public interface IClipWriter
{
    /// <summary>写回剪贴板并返回结果。仅引用失效时不更新剪贴板且不触发防自捕获。</summary>
    ClipWriteResult Write(ClipItem item);
}
