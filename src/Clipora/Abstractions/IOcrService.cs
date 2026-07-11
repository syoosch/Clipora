namespace Clipora.Abstractions;

/// <summary>单次 OCR 识别结果。</summary>
public enum OcrOutcome
{
    /// <summary>识别成功，返回了文字。</summary>
    Recognized,

    /// <summary>识别完成但未发现文字。</summary>
    Empty,

    /// <summary>引擎 / 语言包不可用。</summary>
    Unsupported,

    /// <summary>识别过程异常失败。</summary>
    Failed,
}

/// <summary>OCR 识别结果。</summary>
public readonly record struct OcrResult(OcrOutcome Outcome, string Text);

/// <summary>
/// 图片文字识别服务（M5.1）。
/// 使用 Windows.Media.Ocr 本地引擎，不联网。
/// </summary>
public interface IOcrService
{
    /// <summary>OCR 引擎是否可用（至少一种语言已安装）。</summary>
    bool IsAvailable { get; }

    /// <summary>对指定图片文件执行 OCR 识别。</summary>
    /// <param name="imagePath">完整图片文件路径（RefPath 指向的存储原图）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken ct = default);
}
