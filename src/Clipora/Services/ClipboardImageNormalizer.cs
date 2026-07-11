using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipora.Services;

internal static class ClipboardImageNormalizer
{
    public static BitmapSource NormalizeOpaqueAlpha(BitmapSource source, out bool changed)
    {
        changed = false;
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
            return source;

        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        bool hasVisibleAlpha = false;
        bool hasColorData = false;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            hasColorData |= pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0;
            if (pixels[offset + 3] != 0)
            {
                hasVisibleAlpha = true;
                break;
            }
        }

        if (hasVisibleAlpha || !hasColorData)
            return source;

        for (int offset = 3; offset < pixels.Length; offset += 4)
            pixels[offset] = byte.MaxValue;

        BitmapSource normalized = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        normalized.Freeze();
        changed = true;
        return normalized;
    }

    public static BitmapSource LoadAndRepair(string path, out bool repaired)
    {
        BitmapSource source = Load(path);
        BitmapSource normalized = NormalizeOpaqueAlpha(source, out repaired);
        if (repaired)
            SavePng(normalized, path);
        return normalized;
    }

    public static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        string temporaryPath = path + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = File.Create(temporaryPath))
                encoder.Save(stream);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }
}
