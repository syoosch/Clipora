using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Clipora.Services;

/// <summary>迁移路径重写：DB 三个路径字段 + 文件 manifest StoredPath。</summary>
internal static class StoragePathRewriter
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    /// <summary>重写 staging DB 中 clip_items 的 RefPath / ThumbnailPath / SourceIconPath。
    /// 所有写入值 = final target 下的绝对路径（非 staging 路径）。单事务执行。</summary>
    public static RewriteResult RewriteDatabase(
        string stagingDbPath,
        string sourceRoot,
        string targetRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        targetRoot = Path.GetFullPath(targetRoot);

        string cs = new SqliteConnectionStringBuilder { DataSource = stagingDbPath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();

        using var txn = connection.BeginTransaction();
        try
        {
            // 读取所有需要重写的行
            var rows = new List<(long Id, string? RefPath, string? ThumbnailPath, string? SourceIconPath)>();
            using (var readCmd = connection.CreateCommand())
            {
                readCmd.Transaction = txn;
                readCmd.CommandText = "SELECT Id, RefPath, ThumbnailPath, SourceIconPath FROM clip_items;";
                using var reader = readCmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            // 逐行校验并计算新值
            var updates = new List<(long Id, string? NewRefPath, string? NewThumbnailPath, string? NewSourceIconPath)>();
            foreach (var (id, refPath, thumbPath, iconPath) in rows)
            {
                string? newRef = RewriteManagedPath(refPath, sourceRoot, targetRoot, "RefPath");
                string? newThumb = RewriteManagedPath(thumbPath, sourceRoot, targetRoot, "ThumbnailPath");
                string? newIcon = RewriteManagedPath(iconPath, sourceRoot, targetRoot, "SourceIconPath");
                updates.Add((id, newRef, newThumb, newIcon));
            }

            // 单事务写入
            using (var writeCmd = connection.CreateCommand())
            {
                writeCmd.Transaction = txn;
                writeCmd.CommandText = "UPDATE clip_items SET RefPath = $ref, ThumbnailPath = $thumb, SourceIconPath = $icon WHERE Id = $id;";
                writeCmd.Parameters.Add(new SqliteParameter("$ref", SqliteType.Text));
                writeCmd.Parameters.Add(new SqliteParameter("$thumb", SqliteType.Text));
                writeCmd.Parameters.Add(new SqliteParameter("$icon", SqliteType.Text));
                writeCmd.Parameters.Add(new SqliteParameter("$id", SqliteType.Integer));
                writeCmd.Prepare();

                foreach (var (id, newRef, newThumb, newIcon) in updates)
                {
                    writeCmd.Parameters["$ref"].Value = newRef is null ? DBNull.Value : newRef;
                    writeCmd.Parameters["$thumb"].Value = newThumb is null ? DBNull.Value : newThumb;
                    writeCmd.Parameters["$icon"].Value = newIcon is null ? DBNull.Value : newIcon;
                    writeCmd.Parameters["$id"].Value = id;
                    writeCmd.ExecuteNonQuery();
                }
            }

            txn.Commit();
            return RewriteResult.Ok();
        }
        catch (RewriteResultException rre)
        {
            try { txn.Rollback(); } catch { /* ignore */ }
            return RewriteResult.Fail(rre.Error, rre.Message);
        }
        catch (Exception ex)
        {
            try { txn.Rollback(); } catch { /* ignore */ }
            return RewriteResult.Fail(Models.StorageMigrationError.RebaseFailed, $"数据库路径重写失败: {ex.Message}");
        }
        finally
        {
            connection.Close();
        }
    }

    /// <summary>重写一个文件 manifest 的 Entries[*].StoredPath。原子写入（临时文件 + move）。</summary>
    public static RewriteResult RewriteManifest(
        string manifestPath,
        string sourceRoot,
        string targetRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        targetRoot = Path.GetFullPath(targetRoot);

        try
        {
            ClipFileManifest? manifest = ClipFileManifest.Load(manifestPath);
            if (manifest is null)
                return RewriteResult.Fail(Models.StorageMigrationError.UnsafePath, $"无法解析 manifest: {manifestPath}");

            // 校验
            if (manifest.Entries.Count == 0)
                return RewriteResult.Fail(Models.StorageMigrationError.UnsafePath, $"manifest 条目为空: {manifestPath}");

            foreach (var entry in manifest.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.OriginalPath))
                    return RewriteResult.Fail(Models.StorageMigrationError.UnsafePath, $"manifest entry OriginalPath 为空: {manifestPath}");

                if (manifest.IsReferenceOnly)
                {
                    // reference-only: StoredPath 必须为 null
                    if (entry.StoredPath is not null)
                        return RewriteResult.Fail(Models.StorageMigrationError.UnsafePath, $"仅引用 manifest entry StoredPath 应为 null: {manifestPath}");
                }
                else
                {
                    // 非 reference-only: StoredPath 必须非空且严格位于 source
                    string storedPath = entry.StoredPath!;
                    if (string.IsNullOrWhiteSpace(storedPath))
                        return RewriteResult.Fail(Models.StorageMigrationError.UnsafePath, $"非引用 manifest entry StoredPath 为空: {manifestPath}");

                    string? newStored = RewriteManagedPath(storedPath, sourceRoot, targetRoot, "manifest StoredPath");
                    entry.StoredPath = newStored;
                }
                // OriginalPath 原样保留
            }

            // 原子写入
            string tempPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, manifestPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
                throw;
            }

            return RewriteResult.Ok();
        }
        catch (RewriteResultException rre)
        {
            return RewriteResult.Fail(rre.Error, rre.Message);
        }
        catch (Exception ex)
        {
            return RewriteResult.Fail(Models.StorageMigrationError.RebaseFailed, $"manifest 重写失败: {manifestPath} ({ex.Message})");
        }
    }

    /// <summary>计算单个受管路径的重写值：source 下相对路径 → target 下绝对路径。</summary>
    public static string? RewriteManagedPath(
        string? path,
        string sourceRoot,
        string targetRoot,
        string context)
    {
        // null 保持 null
        if (path is null)
            return null;

        try
        {
            string full = Path.GetFullPath(path);

            // 严格位于 source 下？
            if (!IsStrictlyUnder(full, sourceRoot))
                throw new RewriteResultException(Models.StorageMigrationError.UnsafePath,
                    $"{context} 路径越界（不在 source 下）: {full}");

            // 取相对路径，组合到 target
            string relative = GetRelativePath(sourceRoot, full);
            return Path.GetFullPath(Path.Combine(targetRoot, relative));
        }
        catch (RewriteResultException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RewriteResultException(Models.StorageMigrationError.UnsafePath,
                $"{context} 路径重写失败: {path} ({ex.Message})");
        }
    }

    /// <summary>计算相对路径（source 下）。</summary>
    private static string GetRelativePath(string root, string fullPath)
    {
        // root 和 fullPath 均已 GetFullPath 规范化
        string rootSep = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase))
            throw new RewriteResultException(Models.StorageMigrationError.UnsafePath,
                $"路径不在 Root 下: {fullPath} (Root={root})");

        // 取相对部分
        string relative = fullPath[rootSep.Length..];

        // 安全检查：相对路径不得以 \ 开头，不得包含 traversal
        if (string.IsNullOrEmpty(relative))
            throw new RewriteResultException(Models.StorageMigrationError.UnsafePath,
                $"路径等于 Root: {fullPath}");

        if (relative.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            throw new RewriteResultException(Models.StorageMigrationError.UnsafePath,
                $"相对路径以分隔符开头: {relative}");

        if (relative.Contains("..", StringComparison.Ordinal))
            throw new RewriteResultException(Models.StorageMigrationError.UnsafePath,
                $"相对路径含 traversal: {relative}");

        return relative;
    }

    /// <summary>严格归属判断：规范化后 fullPath 在 rootWithSeparator 前缀下。</summary>
    public static bool IsStrictlyUnder(string fullPath, string root)
    {
        string rootSep = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class RewriteResult
    {
        public bool Success { get; }
        public Models.StorageMigrationError Error { get; }
        public string? Message { get; }

        private RewriteResult(bool success, Models.StorageMigrationError error, string? message)
        {
            Success = success;
            Error = error;
            Message = message;
        }

        public static RewriteResult Ok() => new(true, Models.StorageMigrationError.None, null);
        public static RewriteResult Fail(Models.StorageMigrationError error, string message) => new(false, error, message);
    }

    private sealed class RewriteResultException : Exception
    {
        public Models.StorageMigrationError Error { get; }
        public RewriteResultException(Models.StorageMigrationError error, string message) : base(message)
        {
            Error = error;
        }
    }
}
