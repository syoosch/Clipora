using System;
using System.Collections.Generic;
using System.IO;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>仅引用文件 / 目录的惰性失效检测结果。</summary>
internal readonly record struct ClipFileReferenceValidation(
    bool IsReferenceOnly,
    bool IsValid,
    IReadOnlyList<string> Paths);

/// <summary>
/// 统一验证器：点击重用与拖出前检查仅引用项的原路径是否存在。
/// 遵循零后台扫描（只在调用时才探测路径），多文件原子失败，best-effort 写回 manifest。
/// </summary>
internal static class ClipFileReferenceValidator
{
    /// <summary>惰性验证指定记录的引用有效性。非 File 或非仅引用项直接返回 <c>IsReferenceOnly=false</c> 且不探测路径。</summary>
    public static ClipFileReferenceValidation Validate(ClipItem item)
    {
        if (item.Type != ClipType.File)
            return new ClipFileReferenceValidation(false, true, Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(item.RefPath))
        {
            // 空 RefPath 但 PreviewText 带旧"仅引用"标记 → fail-closed
            if (item.PreviewText.Contains("（仅引用）", StringComparison.Ordinal))
                return new ClipFileReferenceValidation(true, false, Array.Empty<string>());
            return new ClipFileReferenceValidation(false, true, Array.Empty<string>());
        }

        // 加载 manifest
        ClipFileManifest? manifest;
        try { manifest = ClipFileManifest.Load(item.RefPath); }
        catch { manifest = null; }

        // 判断是否为仅引用
        bool isReferenceOnly = DetectReferenceOnly(item, manifest);
        if (!isReferenceOnly)
            return new ClipFileReferenceValidation(false, true, Array.Empty<string>());

        // manifest 缺失/损坏/空条目 → fail-closed
        if (manifest is null || manifest.Entries.Count == 0)
        {
            TrySetInvalid(manifest, item.RefPath, true);
            return new ClipFileReferenceValidation(true, false, Array.Empty<string>());
        }

        // 探测所有 OriginalPath
        var validPaths = new List<string>();
        bool anyMissing = false;

        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalPath))
            {
                anyMissing = true;
                break;
            }

            try
            {
                if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
                    validPaths.Add(entry.OriginalPath);
                else
                {
                    anyMissing = true;
                    break; // 原子失败：任一缺失即终止
                }
            }
            catch
            {
                anyMissing = true;
                break;
            }
        }

        if (anyMissing)
        {
            TrySetInvalid(manifest, item.RefPath, true);
            return new ClipFileReferenceValidation(true, false, Array.Empty<string>());
        }

        // 全部有效
        TrySetInvalid(manifest, item.RefPath, false);
        return new ClipFileReferenceValidation(true, true, validPaths);
    }

    private static bool DetectReferenceOnly(ClipItem item, ClipFileManifest? manifest)
    {
        if (item.PreviewText.Contains("（仅引用）", StringComparison.Ordinal))
            return true;

        return manifest?.IsReferenceOnly == true;
    }

    private static void TrySetInvalid(ClipFileManifest? manifest, string manifestPath, bool isInvalid)
    {
        if (manifest is null)
            return;

        if (manifest.IsReferenceInvalid == isInvalid)
            return;

        manifest.IsReferenceInvalid = isInvalid;

        try
        {
            manifest.Save(manifestPath);
        }
        catch
        {
            // best-effort：文件系统写回失败不崩溃
        }
    }
}
