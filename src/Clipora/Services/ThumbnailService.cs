using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipora.Abstractions;

namespace Clipora.Services;

/// <summary><see cref="IThumbnailService"/> 实现：用 WPF 成像保存图片与缩略图。</summary>
public sealed class ThumbnailService : IThumbnailService
{
    private readonly AppPaths _paths;

    public ThumbnailService(AppPaths paths) => _paths = paths;

    public string SaveImage(BitmapSource image)
    {
        string path = Path.Combine(_paths.ImagesDir, Guid.NewGuid().ToString("N") + ".png");
        BitmapSource normalized = ClipboardImageNormalizer.NormalizeOpaqueAlpha(image, out _);
        ClipboardImageNormalizer.SavePng(normalized, path);
        return path;
    }

    public string CreateThumbnail(BitmapSource image, int maxSize)
    {
        image = ClipboardImageNormalizer.NormalizeOpaqueAlpha(image, out _);
        double longest = Math.Max(image.PixelWidth, image.PixelHeight);
        BitmapSource scaled = image;
        if (longest > maxSize && longest > 0)
        {
            double ratio = maxSize / longest;
            scaled = new TransformedBitmap(image, new ScaleTransform(ratio, ratio));
        }

        string path = Path.Combine(_paths.ThumbsDir, Guid.NewGuid().ToString("N") + ".png");
        ClipboardImageNormalizer.SavePng(scaled, path);
        return path;
    }
}
