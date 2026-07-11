using System;
using System.IO;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>删除单条剪贴板记录在数据目录中的附属文件（副本/缩略图/sidecar/manifest/payload）。
/// 全程 best-effort，绝不抛异常，绝不删用户原文件。</summary>
public sealed class ClipItemFileEraser
{
    private readonly AppPaths _paths;

    public ClipItemFileEraser(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>删除 <paramref name="item"/> 的数据目录附属文件。全程 try/catch，不抛异常。</summary>
    public void Erase(ClipItem item)
    {
        EraseFile(item.ThumbnailPath);
        EraseFile(item.SourceIconPath);

        switch (item.Type)
        {
            case ClipType.Image:
                // Image: RefPath = 原图副本（位于 ImagesDir）
                EraseFile(item.RefPath);
                break;

            case ClipType.RichText:
                // RichText: RefPath = sidecar 文件（位于 RichTextDir）
                EraseFile(item.RefPath);
                break;

            case ClipType.File:
                // File: RefPath = manifest（位于 FileManifestsDir）
                EraseFileManifest(item.RefPath);
                break;

            // Text / URL / Code / Color：无文件附属
        }
    }

    private void EraseFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (IsUnderRoot(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort：单个文件删除失败不影响其他
        }
    }

    private void EraseFileManifest(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            return;

        var manifest = ClipFileManifest.Load(manifestPath);
        if (manifest is null)
        {
            // 清单损坏/不存在，仍尝试删掉清单文件本身
            EraseFile(manifestPath);
            return;
        }

        // IsReferenceOnly = true → 未复制副本，不碰任何 payload
        if (!manifest.IsReferenceOnly)
        {
            foreach (var entry in manifest.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.StoredPath))
                    continue;

                // 安全不变量 1：必须在 FilePayloadsDir 之下
                if (!IsUnderFilePayloadsDir(entry.StoredPath))
                    continue;

                // 安全不变量 2：绝不能等于原始路径
                if (IsSamePath(entry.StoredPath, entry.OriginalPath))
                    continue;

                try
                {
                    File.Delete(entry.StoredPath);
                }
                catch
                {
                    // best-effort
                }

                // 删除 payload 所在子目录（每个 File 项独享一个 guid 子目录）
                string? dir = Path.GetDirectoryName(entry.StoredPath);
                if (!string.IsNullOrWhiteSpace(dir) && IsUnderFilePayloadsDir(dir))
                {
                    try
                    {
                        if (Directory.Exists(dir))
                            Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // 子目录可能被其它 entry 共享，删除失败无害
                    }
                }
            }
        }

        // 最后删除 manifest 文件本身
        EraseFile(manifestPath);
    }

    // 三个路径校验全程 try/catch：畸形路径让 Path.GetFullPath 抛出时一律按
    // "不在受控目录内 / 不相同" 处理（朝安全方向：宁可跳过删除，绝不误删）。
    private bool IsUnderRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(_paths.Root) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, Path.GetFullPath(_paths.Root), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsUnderFilePayloadsDir(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string payloads = Path.GetFullPath(_paths.FilePayloadsDir) + Path.DirectorySeparatorChar;
            return full.StartsWith(payloads, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, Path.GetFullPath(_paths.FilePayloadsDir), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
