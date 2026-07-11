using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary><see cref="IClipWriter"/> 实现：写回剪贴板，并附加内部标记以防自捕获。</summary>
public sealed class ClipWriter : IClipWriter
{
    public ClipWriteResult Write(ClipItem item)
    {
        // File 类型：先惰性检查仅引用有效性
        if (item.Type == ClipType.File)
        {
            ClipFileReferenceValidation validation = ClipFileReferenceValidator.Validate(item);
            if (validation.IsReferenceOnly && !validation.IsValid)
                return ClipWriteResult.ReferenceUnavailable;

            if (validation.IsReferenceOnly && validation.IsValid)
            {
                try
                {
                    WriteFilesFromPaths(validation.Paths);
                }
                catch
                {
                    // 剪贴板被占用时静默失败，下次再试。
                }
                return ClipWriteResult.Completed;
            }
        }

        try
        {
            switch (item.Type)
            {
                case ClipType.Image:
                    WriteImage(item);
                    break;
                case ClipType.RichText:
                    WriteRichText(item);
                    break;
                case ClipType.File:
                    WriteFiles(item);
                    break;
                default:
                    if (!string.IsNullOrEmpty(item.TextContent))
                    {
                        var data = new DataObject();
                        data.SetText(item.TextContent, TextDataFormat.UnicodeText);
                        ClipboardInternalWriteMarker.SetClipboard(data);
                    }
                    break;
            }
        }
        catch
        {
            // 剪贴板被占用时静默失败，下次再试。
        }

        return ClipWriteResult.Completed;
    }

    private static void WriteFilesFromPaths(IReadOnlyList<string> paths)
    {
        var collection = new StringCollection();
        foreach (string path in paths)
            collection.Add(path);

        var data = new DataObject();
        data.SetFileDropList(collection);
        ClipboardInternalWriteMarker.SetClipboard(data);
    }

    private static void WriteImage(ClipItem item)
    {
        if (string.IsNullOrEmpty(item.RefPath) || !File.Exists(item.RefPath))
            return;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(item.RefPath);
        image.EndInit();
        image.Freeze();
        var data = new DataObject();
        data.SetImage(image);
        ClipboardInternalWriteMarker.SetClipboard(data);
    }

    private static void WriteRichText(ClipItem item)
    {
        var data = new DataObject();
        bool hasData = false;

        if (!string.IsNullOrEmpty(item.TextContent))
        {
            data.SetText(item.TextContent, TextDataFormat.UnicodeText);
            hasData = true;
        }

        if (!string.IsNullOrEmpty(item.RefPath) && File.Exists(item.RefPath))
        {
            string richContent = File.ReadAllText(item.RefPath, Encoding.UTF8);
            string extension = Path.GetExtension(item.RefPath);
            if (string.Equals(extension, ".rtf", StringComparison.OrdinalIgnoreCase))
            {
                data.SetData(DataFormats.Rtf, richContent);
                hasData = true;
            }
            else if (string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase))
            {
                data.SetData(DataFormats.Html, richContent);
                hasData = true;
            }
        }

        if (hasData)
            ClipboardInternalWriteMarker.SetClipboard(data);
    }

    private static void WriteFiles(ClipItem item)
    {
        if (string.IsNullOrEmpty(item.RefPath))
            return;

        ClipFileManifest? manifest = ClipFileManifest.Load(item.RefPath);
        IReadOnlyList<string> paths = manifest?.GetAvailablePaths() ?? Array.Empty<string>();
        if (paths.Count == 0)
            return;

        var collection = new StringCollection();
        foreach (string path in paths)
            collection.Add(path);

        var data = new DataObject();
        data.SetFileDropList(collection);
        ClipboardInternalWriteMarker.SetClipboard(data);
    }
}
