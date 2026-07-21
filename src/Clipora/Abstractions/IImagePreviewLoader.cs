using System.Windows.Media.Imaging;

namespace Clipora.Abstractions;

/// <summary>按显示尺寸惰性解码图片预览；失败或取消时返回 <see langword="null"/>。</summary>
public interface IImagePreviewLoader : IDisposable
{
    Task<BitmapSource?> LoadAsync(
        string refPath,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken = default);
}
