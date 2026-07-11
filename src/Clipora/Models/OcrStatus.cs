namespace Clipora.Models;

/// <summary>图片 OCR 处理状态（M5.1）。</summary>
public enum OcrStatus
{
    /// <summary>未考虑 / 非图片类型。</summary>
    None = 0,

    /// <summary>等待识别。</summary>
    Pending = 1,

    /// <summary>识别完成，有文字结果。</summary>
    Completed = 2,

    /// <summary>已识别，无文字内容。</summary>
    Empty = 3,

    /// <summary>识别过程异常失败。</summary>
    Failed = 4,

    /// <summary>OCR 引擎 / 语言包不可用。</summary>
    Unsupported = 5,
}
