using System;
using System.IO;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>解析数据目录及各子路径。优先级：构造参数 > 环境变量 CLIPORA_DATA_DIR > HKCU locator > %LOCALAPPDATA%\Clipora。</summary>
public sealed class AppPaths
{
    public string Root { get; }

    /// <summary>本次解析的 Root 来源。</summary>
    public StorageRootSource RootSource { get; }

    /// <summary>当前 Root 是否允许迁移（仅 Locator / Default 为 true）。</summary>
    public bool CanMigrate { get; }

    public string DbPath => Path.Combine(Root, "clipora.db");
    public string ImagesDir => Path.Combine(Root, "images");
    public string FilesDir => Path.Combine(Root, "files");
    public string FilePayloadsDir => Path.Combine(FilesDir, "payloads");
    public string FileManifestsDir => Path.Combine(FilesDir, "manifests");
    public string RichTextDir => Path.Combine(Root, "richtext");
    public string ThumbsDir => Path.Combine(Root, "thumbs");
    public string IconsDir => Path.Combine(Root, "icons");
    public string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>生产构造：使用 HKCU 注册表 locator。</summary>
    public AppPaths(string? overrideDir = null)
        : this(overrideDir, StorageLocationService.CreateProduction())
    {
    }

    /// <summary>内部注入构造：仅供自检等场景使用可替换的 locator。</summary>
    internal AppPaths(string? overrideDir, IStorageLocationService locationService)
        : this(locationService.Resolve(overrideDir))
    {
    }

    /// <summary>组合根已完成受控解析后，使用同一 resolution 构造全部路径，避免二次解析竞态。</summary>
    internal AppPaths(StorageRootResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        Root = resolution.Root;
        RootSource = resolution.Source;
        CanMigrate = resolution.CanMigrate;

        if (RootSource == StorageRootSource.Locator)
            StorageLocationService.ValidateLocatorRoot(Root);

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDir);
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(FilePayloadsDir);
        Directory.CreateDirectory(FileManifestsDir);
        Directory.CreateDirectory(RichTextDir);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(IconsDir);
    }

    /// <summary>M5.2：获取绝对路径相对于 Root 的相对路径。</summary>
    internal static string GetRelativePath(string root, string absolutePath)
    {
        try
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalized = Path.GetFullPath(absolutePath);
            if (normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return normalized[normalizedRoot.Length..];
        }
        catch { }
        return absolutePath;
    }

}
