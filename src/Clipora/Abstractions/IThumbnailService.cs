using System.Windows.Media.Imaging;

namespace Clipora.Abstractions;

/// <summary>图片副本与缩略图的生成（基于 WPF 成像，无需 System.Drawing）。</summary>
public interface IThumbnailService
{
    /// <summary>把图片以 PNG 存入数据目录，返回文件路径。</summary>
    string SaveImage(BitmapSource image);

    /// <summary>生成等比缩略图（最长边不超过 maxSize），返回文件路径。</summary>
    string CreateThumbnail(BitmapSource image, int maxSize);
}
