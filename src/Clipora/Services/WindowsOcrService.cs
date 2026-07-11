using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Clipora.Abstractions;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace Clipora.Services;

/// <summary>
/// <see cref="IOcrService"/> 的 Windows.Media.Ocr 实现（M5.1）。
/// 使用本地 WinRT OCR 引擎，不联网。
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private const int MaxDimension = 4096;

    /// <inheritdoc/>
    public bool IsAvailable
    {
        get
        {
            try
            {
                // TryCreateFromUserProfileLanguages 返回 OcrEngine?（非 TryXxx 模式）
                if (OcrEngine.TryCreateFromUserProfileLanguages() is not null)
                    return true;
                // 回退到中/英
                if (OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans")) is not null)
                    return true;
                if (OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en")) is not null)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<Abstractions.OcrResult> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);

        OcrEngine? engine =
            OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"))
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));

        if (engine is null)
            return new Abstractions.OcrResult(OcrOutcome.Unsupported, string.Empty);

        try
        {
            // 1. 解码图片为 SoftwareBitmap（WinRT BitmapDecoder）
            using var fileStream = File.OpenRead(imagePath);
            using var randomAccessStream = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(ct);
            SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);

            // 2. 超长边下采样
            if (softwareBitmap.PixelWidth > MaxDimension || softwareBitmap.PixelHeight > MaxDimension)
            {
                double scale = Math.Min(
                    (double)MaxDimension / softwareBitmap.PixelWidth,
                    (double)MaxDimension / softwareBitmap.PixelHeight);
                uint scaledWidth = (uint)Math.Max(1, softwareBitmap.PixelWidth * scale);
                uint scaledHeight = (uint)Math.Max(1, softwareBitmap.PixelHeight * scale);

                // 必须先转换为 Bgra8 才能传给 BitmapEncoder
                using SoftwareBitmap convertible = EnsureBgra8(softwareBitmap);

                using var memStream = new MemoryStream();
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId, memStream.AsRandomAccessStream()).AsTask(ct);
                encoder.BitmapTransform.ScaledWidth = scaledWidth;
                encoder.BitmapTransform.ScaledHeight = scaledHeight;
                encoder.SetSoftwareBitmap(convertible);
                await encoder.FlushAsync().AsTask(ct);

                memStream.Seek(0, SeekOrigin.Begin);
                var scaledDecoder = await BitmapDecoder.CreateAsync(memStream.AsRandomAccessStream()).AsTask(ct);
                var scaled = await scaledDecoder.GetSoftwareBitmapAsync().AsTask(ct);

                softwareBitmap.Dispose();
                softwareBitmap = scaled;
            }

            // 3. 确保格式兼容（OcrEngine 需要 Bgra8 premultiplied）
            using SoftwareBitmap finalBitmap = EnsureBgra8(softwareBitmap);
            if (!ReferenceEquals(finalBitmap, softwareBitmap))
                softwareBitmap.Dispose();

            // 4. 执行 OCR
            WinOcrResult winResult = await engine.RecognizeAsync(finalBitmap).AsTask(ct);

            string text = winResult.Text?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(text)
                ? new Abstractions.OcrResult(OcrOutcome.Empty, string.Empty)
                : new Abstractions.OcrResult(OcrOutcome.Recognized, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new Abstractions.OcrResult(OcrOutcome.Failed, string.Empty);
        }
    }

    private static SoftwareBitmap EnsureBgra8(SoftwareBitmap source)
    {
        if (source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            && source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
            return source;

        return SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
