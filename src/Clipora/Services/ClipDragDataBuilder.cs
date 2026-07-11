using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using Clipora.Models;

namespace Clipora.Services;

internal static class ClipDragDataBuilder
{
    /// <summary>兼容重载：等价于 <c>TryBuild(item, out data, out _)</c>。</summary>
    public static bool TryBuild(ClipItem item, out DataObject? data)
    {
        return TryBuild(item, out data, out _);
    }

    /// <summary>构造拖放数据，并报告是否因仅引用失效而无法构造。</summary>
    public static bool TryBuild(ClipItem item, out DataObject? data, out bool referenceUnavailable)
    {
        referenceUnavailable = false;

        if (item.Type == ClipType.File)
        {
            data = BuildFiles(item, out referenceUnavailable);
            return data is not null;
        }

        data = item.Type switch
        {
            ClipType.Image => BuildImage(item),
            ClipType.RichText => BuildRichText(item),
            _ => BuildText(item),
        };
        return data is not null;
    }

    private static DataObject? BuildText(ClipItem item)
    {
        if (string.IsNullOrEmpty(item.TextContent))
            return null;

        var data = new DataObject();
        SetCompatibleText(data, item.TextContent);
        return data;
    }

    private static DataObject? BuildRichText(ClipItem item)
    {
        var data = new DataObject();
        bool hasData = false;

        if (!string.IsNullOrEmpty(item.TextContent))
        {
            SetCompatibleText(data, item.TextContent);
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

        return hasData ? data : null;
    }

    private static DataObject? BuildFiles(ClipItem item, out bool referenceUnavailable)
    {
        referenceUnavailable = false;

        ClipFileReferenceValidation validation = ClipFileReferenceValidator.Validate(item);

        if (validation.IsReferenceOnly && !validation.IsValid)
        {
            referenceUnavailable = true;
            return null;
        }

        IReadOnlyList<string> paths;
        if (validation.IsReferenceOnly && validation.IsValid)
        {
            // 使用验证器返回的完整原路径列表，不再二次过滤
            paths = validation.Paths;
        }
        else
        {
            // 非仅引用：继续使用原 GetAvailablePaths 路径（优先 StoredPath）
            if (string.IsNullOrEmpty(item.RefPath))
                return null;

            paths = ClipFileManifest.Load(item.RefPath)?.GetAvailablePaths()
                ?? Array.Empty<string>();
        }

        if (paths.Count == 0)
            return null;

        var files = new StringCollection();
        foreach (string path in paths)
            files.Add(path);

        var data = new DataObject();
        data.SetFileDropList(files);
        SetPreferredCopyEffect(data);
        return data;
    }

    private static DataObject? BuildImage(ClipItem item)
    {
        if (string.IsNullOrEmpty(item.RefPath) || !File.Exists(item.RefPath))
            return null;

        var data = new DataObject();
        var files = new StringCollection { item.RefPath };
        data.SetFileDropList(files);
        SetPreferredCopyEffect(data);

        try
        {
            var image = ClipboardImageNormalizer.LoadAndRepair(item.RefPath, out _);
            data.SetImage(image);
        }
        catch
        {
            // FileDrop remains usable even if bitmap decoding fails.
        }

        return data;
    }

    private static void SetCompatibleText(DataObject data, string text)
    {
        data.SetData(DataFormats.UnicodeText, text, autoConvert: false);
        data.SetData(DataFormats.Text, text, autoConvert: false);
    }

    private static void SetPreferredCopyEffect(DataObject data)
    {
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
    }
}
