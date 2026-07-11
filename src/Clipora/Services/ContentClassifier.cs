using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>
/// <see cref="IContentClassifier"/> 实现：统一分类文字、富文本、图片与 FileDrop；
/// 外部 IDataObject 入口支持显式 FileDrop、位图与纯文本格式。
/// </summary>
public sealed class ContentClassifier : IContentClassifier
{
    private const int PreviewMax = 200;
    private static readonly Regex UrlRegex =
        new(@"^https?://\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexColorOnlyRegex =
        new(@"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{3})$", RegexOptions.Compiled);
    private static readonly Regex RgbColorOnlyRegex =
        new(@"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RgbaColorOnlyRegex =
        new(@"^rgba\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(0|1|0?\.\d+)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CodeKeywordRegex =
        new(@"\b(?:async|await|case|catch|class|const|def|else|enum|for|function|if|import|interface|let|namespace|new|private|protected|public|return|struct|switch|throw|try|using|var|while)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeCallOrAssignmentRegex =
        new(@"(?:\b[\w$\.]+\s*\([^()\r\n]*\)|(?:^|[\s,(])\b[\w$]+\s*(?:=|=>|\+=|-=|\*=|/=))", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex JsonPropertyRegex =
        new("\"[^\"\r\n]+\"\\s*:", RegexOptions.Compiled);
    private static readonly Regex CssDeclarationRegex =
        new(@"(?:^|[;{])\s*(?:--)?[\w-]+\s*:\s*[^;\r\n{}]+;", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MarkupElementRegex =
        new(@"<([A-Za-z][\w:-]*)\b[^>]*>[\s\S]*</\1\s*>|<[A-Za-z][\w:-]*\b[^>]*/>", RegexOptions.Compiled);
    private static readonly string[] KnownCodeEditorNames =
    {
        "Visual Studio Code",
        "Code - Insiders",
        "Microsoft Visual Studio",
        "JetBrains Rider",
        "Cursor",
    };

    private readonly IThumbnailService _thumbnails;
    private readonly ISourceResolver _source;
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;

    public ContentClassifier(IThumbnailService thumbnails, ISourceResolver source, AppPaths paths, ISettingsService settings)
    {
        _thumbnails = thumbnails;
        _source = source;
        _paths = paths;
        _settings = settings;
    }

    public ClipItem? Classify()
    {
        string? sourceApp = _source.GetClipboardOwnerAppName();

        if (Clipboard.ContainsFileDropList())
        {
            var item = TryClassifyFileDrop(sourceApp);
            if (item is not null)
                return item;
        }

        if (Clipboard.ContainsImage())
        {
            var item = TryClassifyImage(sourceApp);
            if (item is not null)
                return item;
        }

        if (Clipboard.ContainsText())
        {
            var item = TryClassifyPlainTextOverride(sourceApp);
            if (item is not null)
                return item;
        }

        if (Clipboard.ContainsText(TextDataFormat.Rtf) || Clipboard.ContainsText(TextDataFormat.Html))
        {
            var item = TryClassifyRichText(sourceApp);
            if (item is not null)
                return item;
        }

        if (Clipboard.ContainsText())
            return ClassifyText(sourceApp);

        return null;
    }

    public ClipItem? Classify(IDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        if (dataObject.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
        {
            try
            {
                object? raw = dataObject.GetData(DataFormats.FileDrop, autoConvert: false);
                IEnumerable<string>? paths = raw switch
                {
                    string[] array => array,
                    StringCollection collection => collection.Cast<string>(),
                    _ => null,
                };
                ClipItem? fileItem = paths is null ? null : TryClassifyFileDrop(paths, sourceApp: null);
                if (fileItem is not null)
                    return fileItem;
            }
            catch
            {
                // 无效 FileDrop 继续尝试同一 DataObject 中的纯文本 fallback。
            }
        }

        BitmapSource? image = ExternalImageDataReader.TryRead(dataObject);
        if (image is not null)
            return CreateImageItem(image, sourceApp: null);

        string? text = TryGetText(dataObject, DataFormats.UnicodeText)
            ?? TryGetText(dataObject, DataFormats.Text)
            ?? TryGetText(dataObject, DataFormats.StringFormat);
        if (!string.IsNullOrWhiteSpace(text))
        {
            ClipType type = DetectTextType(text);
            string content = type == ClipType.Color && TryNormalizeColorCode(text.Trim(), out string color)
                ? color
                : text;
            return CreateTextItem(type, content, sourceApp: null);
        }

        return TryClassifyRichText(dataObject, sourceApp: null);
    }

    private ClipItem? TryClassifyPlainTextOverride(string? sourceApp)
    {
        string? text = TryGetText(TextDataFormat.UnicodeText)
            ?? TryGetText(TextDataFormat.Text);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string trimmed = text.Trim();
        if (IsUrlOnly(trimmed))
            return CreateTextItem(ClipType.Url, trimmed, sourceApp);

        if (TryNormalizeColorCode(trimmed, out string color))
            return CreateTextItem(ClipType.Color, color, sourceApp);

        if (IsKnownCodeEditor(sourceApp) && LooksLikeEditorCode(trimmed))
            return CreateTextItem(ClipType.Code, text, sourceApp);

        return null;
    }

    private ClipItem? TryClassifyFileDrop(string? sourceApp)
    {
        StringCollection files;
        try { files = Clipboard.GetFileDropList(); }
        catch { return null; }

        return TryClassifyFileDrop(files.Cast<string>(), sourceApp);
    }

    private ClipItem? TryClassifyFileDrop(IEnumerable<string> paths, string? sourceApp)
    {
        string[] originalPaths = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (originalPaths.Length == 0)
            return null;

        var entries = originalPaths.Select(CreateFileEntry).ToList();
        long totalBytes = entries.Sum(e => e.SizeBytes);
        bool hasDirectory = entries.Any(e => e.IsDirectory);
        bool copyFiles = !hasDirectory
            && _settings.Current.MaxItemBytes > 0
            && totalBytes < _settings.Current.MaxItemBytes;

        if (copyFiles)
        {
            string payloadDir = Path.Combine(_paths.FilePayloadsDir, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(payloadDir);
                var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ClipFileManifestEntry entry in entries)
                {
                    string targetPath = MakeUniqueFilePath(payloadDir, entry.DisplayName, usedPaths);
                    File.Copy(entry.OriginalPath, targetPath, overwrite: false);
                    entry.StoredPath = targetPath;
                }
            }
            catch
            {
                try { Directory.Delete(payloadDir, recursive: true); } catch { }
                copyFiles = false;
                foreach (ClipFileManifestEntry entry in entries)
                    entry.StoredPath = null;
            }
        }

        var manifest = new ClipFileManifest
        {
            IsReferenceOnly = !copyFiles,
            Entries = entries,
        };

        string manifestPath = Path.Combine(_paths.FileManifestsDir, Guid.NewGuid().ToString("N") + ".clipora-files.json");
        manifest.Save(manifestPath);

        return new ClipItem
        {
            Type = ClipType.File,
            PreviewText = MakeFilePreview(entries, manifest.IsReferenceOnly),
            RefPath = manifestPath,
            ThumbnailPath = TryCreateImageFileThumbnail(entries),
            SourceApp = sourceApp,
            SizeBytes = totalBytes,
            ContentHash = HashText("filedrop:" + BuildFileHashInput(entries)),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static readonly HashSet<string> RenderableImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
    };

    /// <summary>单个可渲染图片文件：生成缩略图，让文件卡片能像图片一样预览（仍是 File 类型、保留原文件身份）。</summary>
    private string? TryCreateImageFileThumbnail(IReadOnlyList<ClipFileManifestEntry> entries)
    {
        if (entries.Count != 1)
            return null;

        ClipFileManifestEntry entry = entries[0];
        if (entry.IsDirectory || !RenderableImageExtensions.Contains(Path.GetExtension(entry.DisplayName)))
            return null;

        string? source = !string.IsNullOrWhiteSpace(entry.StoredPath) ? entry.StoredPath : entry.OriginalPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return null;

        try
        {
            BitmapSource image = ClipboardImageNormalizer.Load(source);
            return _thumbnails.CreateThumbnail(image, 240);
        }
        catch
        {
            return null;   // 无法解码（损坏/不支持的编码）则退回纯文件卡片
        }
    }

    private ClipItem? TryClassifyRichText(string? sourceApp)
    {
        string? rtf = TryGetText(TextDataFormat.Rtf);
        string? html = TryGetText(TextDataFormat.Html);
        if (string.IsNullOrWhiteSpace(rtf) && string.IsNullOrWhiteSpace(html))
            return null;

        string plainText = TryGetText(TextDataFormat.UnicodeText)
            ?? TryGetText(TextDataFormat.Text)
            ?? ExtractRichText(rtf, html);
        return CreateRichTextItem(rtf, html, plainText, sourceApp);
    }

    private ClipItem? TryClassifyRichText(IDataObject dataObject, string? sourceApp)
    {
        string? rtf = TryGetText(dataObject, DataFormats.Rtf);
        string? html = TryGetText(dataObject, DataFormats.Html);
        if (string.IsNullOrWhiteSpace(rtf) && string.IsNullOrWhiteSpace(html))
            return null;

        return CreateRichTextItem(rtf, html, ExtractRichText(rtf, html), sourceApp);
    }

    private ClipItem CreateRichTextItem(string? rtf, string? html, string plainText, string? sourceApp)
    {
        bool useRtf = !string.IsNullOrWhiteSpace(rtf);
        string richContent = useRtf ? rtf! : html!;
        string extension = useRtf ? ".rtf" : ".html";
        string richPath = Path.Combine(_paths.RichTextDir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(richPath, richContent, Encoding.UTF8);

        string preview = string.IsNullOrWhiteSpace(plainText) ? "富文本" : MakePreview(plainText);
        long sidecarBytes = new FileInfo(richPath).Length;

        return new ClipItem
        {
            Type = ClipType.RichText,
            PreviewText = preview,
            TextContent = plainText,
            RefPath = richPath,
            SourceApp = sourceApp,
            SizeBytes = sidecarBytes + Encoding.UTF8.GetByteCount(plainText),
            ContentHash = HashText("rich:" + extension + ":" + richContent),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static string ExtractRichText(string? rtf, string? html)
    {
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            try
            {
                var document = new FlowDocument();
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
                range.Load(stream, DataFormats.Rtf);
                return range.Text.Trim();
            }
            catch
            {
                // 无法解析的 RTF 仍保存原始 sidecar，预览退回“富文本”。
            }
        }

        return string.IsNullOrWhiteSpace(html) ? string.Empty : StripHtml(html);
    }

    private ClipItem? TryClassifyImage(string? sourceApp)
    {
        try
        {
            var image = Clipboard.GetImage();
            return image is null ? null : CreateImageItem(image, sourceApp);
        }
        catch
        {
            return null;
        }
    }

    private ClipItem? CreateImageItem(BitmapSource image, string? sourceApp)
    {
        try
        {
            string imagePath = _thumbnails.SaveImage(image);
            string thumbPath = _thumbnails.CreateThumbnail(image, 240);
            long size = new FileInfo(imagePath).Length;

            return new ClipItem
            {
                Type = ClipType.Image,
                PreviewText = $"图片 {image.PixelWidth}×{image.PixelHeight}",
                RefPath = imagePath,
                ThumbnailPath = thumbPath,
                SourceApp = sourceApp,
                SizeBytes = size,
                ContentHash = HashFile(imagePath),
                CreatedAt = DateTime.UtcNow,
                OcrStatus = OcrStatus.Pending,
            };
        }
        catch
        {
            return null;
        }
    }

    private ClipItem? ClassifyText(string? sourceApp)
    {
        string? text = TryGetText(TextDataFormat.UnicodeText)
            ?? TryGetText(TextDataFormat.Text);

        if (string.IsNullOrEmpty(text))
            return null;

        ClipType type = DetectTextType(text);
        string content = type == ClipType.Color && TryNormalizeColorCode(text.Trim(), out string color)
            ? color
            : text;
        return CreateTextItem(type, content, sourceApp);
    }

    private static ClipItem CreateTextItem(ClipType type, string text, string? sourceApp) =>
        new()
        {
            Type = type,
            PreviewText = MakePreview(text),
            TextContent = text,
            SourceApp = sourceApp,
            SizeBytes = Encoding.UTF8.GetByteCount(text),
            ContentHash = HashText(text),
            CreatedAt = DateTime.UtcNow,
        };

    private static ClipType DetectTextType(string text)
    {
        string trimmed = text.Trim();
        if (IsUrlOnly(trimmed))
            return ClipType.Url;
        if (TryNormalizeColorCode(trimmed, out _))
            return ClipType.Color;
        if (LooksLikeCode(trimmed))
            return ClipType.Code;
        return ClipType.Text;
    }

    private static bool IsUrlOnly(string text) =>
        !text.Contains(' ') && !text.Contains('\n') && UrlRegex.IsMatch(text);

    private static bool TryNormalizeColorCode(string text, out string normalized)
    {
        normalized = string.Empty;
        Match hex = HexColorOnlyRegex.Match(text);
        if (hex.Success)
        {
            string value = hex.Value[1..];
            if (value.Length == 3)
                value = string.Concat(value.Select(c => new string(c, 2)));
            normalized = "#" + value.ToUpperInvariant();
            return true;
        }

        Match rgb = RgbColorOnlyRegex.Match(text);
        if (rgb.Success && TryReadRgb(rgb, out int r, out int g, out int b))
        {
            normalized = $"rgb({r}, {g}, {b})";
            return true;
        }

        Match rgba = RgbaColorOnlyRegex.Match(text);
        if (rgba.Success && TryReadRgb(rgba, out r, out g, out b))
        {
            normalized = $"rgba({r}, {g}, {b}, {rgba.Groups[4].Value})";
            return true;
        }

        return false;
    }

    private static bool TryReadRgb(Match match, out int r, out int g, out int b)
    {
        r = g = b = 0;
        return int.TryParse(match.Groups[1].Value, out r) && r <= 255
            && int.TryParse(match.Groups[2].Value, out g) && g <= 255
            && int.TryParse(match.Groups[3].Value, out b) && b <= 255;
    }

    private static bool LooksLikeCode(string text)
    {
        string[] markers =
        {
            ";", "{", "}", "=>", "()", "import ", "def ", "function ",
            "public ", "private ", "const ", "var ", "let ", "#include", "class ", "</",
        };

        int hits = 0;
        foreach (var marker in markers)
            if (text.Contains(marker, StringComparison.Ordinal))
                hits++;

        return hits >= 2 && text.Length <= 20000;
    }

    private static bool IsKnownCodeEditor(string? sourceApp)
    {
        if (string.IsNullOrWhiteSpace(sourceApp))
            return false;

        return KnownCodeEditorNames.Any(name => sourceApp.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeEditorCode(string text)
    {
        if (text.Length == 0 || text.Length > 20000)
            return false;

        if (MarkupElementRegex.IsMatch(text))
            return true;

        int signals = 0;
        if (CodeKeywordRegex.IsMatch(text))
            signals++;
        if (CodeCallOrAssignmentRegex.IsMatch(text))
            signals++;
        if (JsonPropertyRegex.IsMatch(text) || CssDeclarationRegex.IsMatch(text))
            signals++;
        if (text.Contains(';') || text.Contains('{') || text.Contains('}') || text.Contains("=>", StringComparison.Ordinal))
            signals++;
        if (text.Contains("//", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal))
            signals++;
        if ((text.Contains('\r') || text.Contains('\n'))
            && text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Length > 0 && char.IsWhiteSpace(line[0])))
        {
            signals++;
        }

        return signals >= 2;
    }

    private static string MakePreview(string text)
    {
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= PreviewMax ? oneLine : oneLine[..PreviewMax] + "…";
    }

    private static ClipFileManifestEntry CreateFileEntry(string path)
    {
        bool isDirectory = Directory.Exists(path);
        long sizeBytes = 0;
        if (!isDirectory && File.Exists(path))
        {
            try { sizeBytes = new FileInfo(path).Length; }
            catch { sizeBytes = 0; }
        }

        string displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = path;

        return new ClipFileManifestEntry
        {
            OriginalPath = path,
            DisplayName = displayName,
            IsDirectory = isDirectory,
            SizeBytes = sizeBytes,
        };
    }

    internal static string MakeFilePreview(IReadOnlyList<ClipFileManifestEntry> entries, bool _)
    {
        if (entries.Count == 1)
            return entries[0].DisplayName;

        string label = MakeFileCountLabel(entries);
        return $"{label}\n{entries[0].DisplayName}\n{entries[1].DisplayName}";
    }

    internal static string MakeFileCountLabel(IReadOnlyList<ClipFileManifestEntry> entries)
    {
        int folders = entries.Count(e => e.IsDirectory);
        int files = entries.Count - folders;
        return folders == 0 ? $"共 {files} 个文件" :
            files == 0 ? $"共 {folders} 个文件夹" :
            $"共 {files} 个文件 + {folders} 个文件夹";
    }

    private static string MakeUniqueFilePath(string directory, string fileName, HashSet<string> usedPaths)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "file";

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(directory, fileName);
        int index = 2;
        while (usedPaths.Contains(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            index++;
        }

        usedPaths.Add(candidate);
        return candidate;
    }

    private static string BuildFileHashInput(IReadOnlyList<ClipFileManifestEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (ClipFileManifestEntry entry in entries)
        {
            builder.Append(entry.OriginalPath).Append('|')
                .Append(entry.SizeBytes).Append('|')
                .Append(entry.IsDirectory).Append('|');
            if (File.Exists(entry.OriginalPath))
            {
                try { builder.Append(File.GetLastWriteTimeUtc(entry.OriginalPath).Ticks); }
                catch { builder.Append('0'); }
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string? TryGetText(TextDataFormat format)
    {
        try
        {
            return Clipboard.ContainsText(format) ? Clipboard.GetText(format) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetText(IDataObject dataObject, string format)
    {
        try
        {
            return dataObject.GetDataPresent(format, autoConvert: false)
                ? dataObject.GetData(format, autoConvert: false) as string
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StripHtml(string html)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        int start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        int end = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
            html = html[(start + startMarker.Length)..end];

        string noTags = HtmlTagRegex.Replace(html, " ");
        return Regex.Replace(WebUtility.HtmlDecode(noTags), @"\s+", " ").Trim();
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
