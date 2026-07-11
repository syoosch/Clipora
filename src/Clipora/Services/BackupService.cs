using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>
/// <see cref="IBackupService"/> 实现。
/// 导出：VACUUM INTO 一致快照 → 打包受管 payload → ZIP → 原子 Move → SHA-256 manifest。
/// 导入：预检 → staging → 按 ContentHash 去重合并 → 单事务提交 → 崩溃恢复。
/// </summary>
public sealed class BackupService : IBackupService
{
    private const int FormatVersion = 1;

    private readonly AppPaths _paths;
    private readonly Database _db;

    public BackupService(AppPaths paths, Database db)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ══════════════════════════════════════════════
    //  导出
    // ══════════════════════════════════════════════

    public async Task<BackupExportResult> ExportAsync(
        string destFilePath, IProgress<BackupProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destFilePath);

        string? tempDir = null;
        string? tempArchive = null;
        try
        {
            progress?.Report(new(BackupPhase.Preparing, 0, 0));
            string destDir = Path.GetDirectoryName(Path.GetFullPath(destFilePath))
                ?? throw new ArgumentException("目标路径无效", nameof(destFilePath));

            // 1. 创建临时工作目录
            tempDir = Path.Combine(Path.GetTempPath(), $"clipora-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string tempDb = Path.Combine(tempDir, "clipora.db");

            // 2. WAL checkpoint + VACUUM INTO 取一致快照
            progress?.Report(new(BackupPhase.CopyingDatabase, 0, 0));
            _db.CheckpointAndVacuumInto(tempDb);
            ct.ThrowIfCancellationRequested();

            // 3. 在临时快照中剔除回收站行及孤标签关系，再 VACUUM
            using (var snapConn = OpenTempDb(tempDb))
            {
                using var clean = snapConn.CreateCommand();
                clean.CommandText = @"
                    DELETE FROM clip_item_tags WHERE ClipItemId IN (SELECT Id FROM clip_items WHERE IsDeleted=1);
                    DELETE FROM clip_items WHERE IsDeleted=1;
                ";
                clean.ExecuteNonQuery();

                // 清理未关联任何 clip_item 的标签
                using var orphanCmd = snapConn.CreateCommand();
                orphanCmd.CommandText = "DELETE FROM tags WHERE Id NOT IN (SELECT DISTINCT TagId FROM clip_item_tags);";
                orphanCmd.ExecuteNonQuery();

                using var vacuum = snapConn.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }
            ct.ThrowIfCancellationRequested();

            // 4. 从临时快照读取活动行，收集受管 payload 路径
            var (items, tags, tagMappings) = ReadActiveItemsFromSnapshot(tempDb);
            var payloadEntries = new List<(string RelativePath, string AbsolutePath)>();
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ClipItem item in items)
            {
                CollectPayloadPaths(item, _paths.Root, referencedPaths);
            }
            ct.ThrowIfCancellationRequested();

            // 5. 计算每个 payload 的 SHA-256 并写入 manifest
            progress?.Report(new(BackupPhase.CopyingFiles, 0, referencedPaths.Count));
            var manifest = new BackupManifest
            {
                FormatVersion = FormatVersion,
                SchemaVersion = GetCurrentSchemaVersion(),
                AppVersion = GetAppVersion(),
                CreatedAtUtc = DateTime.UtcNow,
                ItemCount = items.Count,
                IncludesPayloads = true,
            };

            int fileIndex = 0;
            foreach (string absPath in referencedPaths.OrderBy(p => p))
            {
                ct.ThrowIfCancellationRequested();
                string relPath = AppPaths.GetRelativePath(_paths.Root, absPath);

                // 跳过数据库和 settings（另行处理）
                if (relPath == "clipora.db" || relPath == "settings.json")
                    continue;

                long length = new FileInfo(absPath).Length;
                string sha256 = ComputeSha256(absPath);
                manifest.Entries.Add(new BackupManifestEntry(relPath, length, sha256));

                payloadEntries.Add((relPath, absPath));
                fileIndex++;
                progress?.Report(new(BackupPhase.CopyingFiles, fileIndex, referencedPaths.Count));
            }

            // 6. 计算 clipora.db 的 SHA-256 并写入 manifest
            long dbLength = new FileInfo(tempDb).Length;
            string dbSha256 = ComputeSha256(tempDb);
            manifest.Entries.Add(new BackupManifestEntry("clipora.db", dbLength, dbSha256));

            // 写 manifest.json 到 temp 目录
            string manifestPath = Path.Combine(tempDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonContext.Default.BackupManifest),
                Encoding.UTF8);

            // 7. 打包 ZIP
            progress?.Report(new(BackupPhase.Packaging, 0, 0));
            tempArchive = Path.Combine(destDir, $".{Path.GetFileName(destFilePath)}.tmp");
            if (File.Exists(tempArchive))
                File.Delete(tempArchive);

            using (var zip = ZipFile.Open(tempArchive, ZipArchiveMode.Create))
            {
                // manifest.json
                zip.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.Optimal);
                // clipora.db
                zip.CreateEntryFromFile(tempDb, "clipora.db", CompressionLevel.Optimal);
                // payloads
                foreach (var (relPath, absPath) in payloadEntries)
                {
                    zip.CreateEntryFromFile(absPath, $"payloads/{relPath.Replace('\\', '/')}",
                        CompressionLevel.Optimal);
                }
            }
            ct.ThrowIfCancellationRequested();

            // 8. 校验可读
            progress?.Report(new(BackupPhase.Validating, 0, 0));
            using (var verifyZip = ZipFile.OpenRead(tempArchive))
            {
                foreach (var entry in manifest.Entries)
                {
                    var zipEntry = verifyZip.GetEntry(
                        entry.RelativePath == "clipora.db" ? "clipora.db"
                        : $"payloads/{entry.RelativePath.Replace('\\', '/')}");
                    if (zipEntry is null)
                        throw new InvalidOperationException($"归档校验失败：缺少条目 {entry.RelativePath}");
                }
            }

            // 9. 原子 Move
            if (File.Exists(destFilePath))
                File.Delete(destFilePath);
            File.Move(tempArchive, destFilePath);
            tempArchive = null; // 已移动，不再清理

            progress?.Report(new(BackupPhase.Finalizing, items.Count, items.Count));
            return new BackupExportResult(true, items.Count, new FileInfo(destFilePath).Length, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupExportResult(false, 0, 0, ex.Message);
        }
        finally
        {
            // 清理临时产物
            if (tempArchive is not null)
            {
                try { File.Delete(tempArchive); } catch { }
            }
            if (tempDir is not null)
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    // ══════════════════════════════════════════════
    //  预检
    // ══════════════════════════════════════════════

    public Task<BackupPreview> InspectAsync(string archivePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archivePath);
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var manifestEntry = zip.GetEntry("manifest.json")
                ?? throw new InvalidOperationException("归档缺少 manifest.json");

            using var manifestStream = manifestEntry.Open();
            var manifest = JsonSerializer.Deserialize(manifestStream, JsonContext.Default.BackupManifest)
                ?? throw new InvalidOperationException("manifest.json 无效");

            bool compatible = manifest.FormatVersion <= FormatVersion;
            string? incompatibility = compatible ? null
                : $"格式版本 {manifest.FormatVersion} 不被当前版本（最高 {FormatVersion}）支持";

            return Task.FromResult(new BackupPreview(
                compatible, manifest.FormatVersion, manifest.ItemCount,
                manifest.CreatedAtUtc, incompatibility));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new BackupPreview(false, 0, 0, DateTime.MinValue,
                $"无法读取归档: {ex.Message}"));
        }
    }

    // ══════════════════════════════════════════════
    //  导入
    // ══════════════════════════════════════════════

    public async Task<BackupImportResult> ImportAsync(
        string archivePath, IProgress<BackupProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archivePath);

        Guid importId = Guid.NewGuid();
        string? stagingRoot = null;
        string? journalPath = null;
        var finalPaths = new List<string>();

        try
        {
            // 1. InspectAsync 预检
            progress?.Report(new(BackupPhase.Preparing, 0, 0));
            var preview = await InspectAsync(archivePath, ct);
            if (!preview.Compatible)
                return new BackupImportResult(false, 0, 0, preview.Incompatibility);
            ct.ThrowIfCancellationRequested();

            // 2. ZIP 预检：Zip Slip + 条目数/大小限制
            using var zip = ZipFile.OpenRead(archivePath);
            const long maxEntryCount = 100000;
            const long maxTotalBytes = 10L * 1024 * 1024 * 1024; // 10GB
            long totalUncompressed = 0;

            if (zip.Entries.Count > maxEntryCount)
                return new BackupImportResult(false, 0, 0, $"归档条目数 {zip.Entries.Count} 超过上限 {maxEntryCount}");

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                totalUncompressed += entry.Length;
                if (totalUncompressed > maxTotalBytes)
                    return new BackupImportResult(false, 0, 0, "归档展开大小超过 10GB 上限");
                if (IsZipSlip(entry.FullName))
                    return new BackupImportResult(false, 0, 0, $"Zip Slip 拒绝: {entry.FullName}");
            }

            // 3. 创建 staging 目录
            stagingRoot = Path.Combine(_paths.Root, ".backup-import-staging", importId.ToString("D"));
            Directory.CreateDirectory(stagingRoot);

            // 4. 解压到 staging
            progress?.Report(new(BackupPhase.CopyingFiles, 0, zip.Entries.Count));
            int fileIdx = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string destPath = Path.Combine(stagingRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                // 纵深防御：规范化后的目标必须仍位于 staging 内，
                // 拦截前置扫描遗漏的任何 Zip Slip 变体（绝对路径/规范化绕过）。
                string stagingFull = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(destPath).StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                    return new BackupImportResult(false, 0, 0, $"Zip Slip 拒绝: {entry.FullName}");
                if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: false);
                    fileIdx++;
                    progress?.Report(new(BackupPhase.CopyingFiles, fileIdx, zip.Entries.Count));
                }
            }

            // 5. 读 manifest.json（从 staging）
            string stagingManifestPath = Path.Combine(stagingRoot, "manifest.json");
            var manifest = JsonSerializer.Deserialize<BackupManifest>(
                File.ReadAllText(stagingManifestPath, Encoding.UTF8), JsonContext.Default.BackupManifest)!;

            // 6. SHA-256 校验
            progress?.Report(new(BackupPhase.Validating, 0, manifest.Entries.Count));
            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = manifest.Entries[i];
                string absPath = e.RelativePath == "clipora.db"
                    ? Path.Combine(stagingRoot, "clipora.db")
                    : Path.Combine(stagingRoot, "payloads", e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absPath))
                    return new BackupImportResult(false, 0, 0, $"SHA-256 校验失败：{e.RelativePath} 缺失");
                string actual = ComputeSha256(absPath);
                if (!string.Equals(actual, e.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new BackupImportResult(false, 0, 0, $"SHA-256 校验失败：{e.RelativePath}");
                progress?.Report(new(BackupPhase.Validating, i + 1, manifest.Entries.Count));
            }

            // 7. 为 payload 分配最终唯一名（不覆盖现有文件）
            //    同时记录「归档相对路径 → 实际落地 finalPath」映射，供 DB 路径列重链。
            var payloadMoves = new List<(string StagingPath, string FinalPath)>();
            var relToFinal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (entry.RelativePath == "clipora.db" || entry.RelativePath == "manifest.json")
                    continue;
                string stagingPath = Path.Combine(stagingRoot, "payloads",
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                string finalPath = MakeUniqueFinalPath(entry.RelativePath);
                payloadMoves.Add((stagingPath, finalPath));
                finalPaths.Add(finalPath);

                string relKey = entry.RelativePath
                    .Replace('/', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);
                relToFinal[relKey] = finalPath;
            }

            // 8. 写 journal
            journalPath = Path.Combine(stagingRoot, "import-journal.json");
            var journal = new BackupImportJournal
            {
                ImportId = importId.ToString("D"),
                StagingRoot = stagingRoot,
                FinalPaths = finalPaths,
                Phase = "pre_commit",
            };
            File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, JsonContext.Default.BackupImportJournal), Encoding.UTF8);

            // 9. 合并式去重导入
            progress?.Report(new(BackupPhase.Merging, 0, manifest.ItemCount));
            int imported = 0, skipped = 0;
            int itemIdx = 0;

            using (var conn = _db.Open())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    // Move payloads 到最终路径（同卷原子）
                    foreach (var (stagingPath, finalPath) in payloadMoves)
                    {
                        ct.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                        File.Move(stagingPath, finalPath, overwrite: false);
                    }

                    // 读取 staging clipora.db 的活动行
                    string stagingDb = Path.Combine(stagingRoot, "clipora.db");
                    var (items, tags, tagMappings) = ReadActiveItemsFromSnapshot(stagingDb);

                    // 标签重映射：Name→Id
                    var tagNameMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    using (var tagCmd = conn.CreateCommand())
                    {
                        tagCmd.CommandText = "SELECT Id, Name FROM tags;";
                        using var r = tagCmd.ExecuteReader();
                        while (r.Read())
                            tagNameMap[r.GetString(1)] = r.GetInt64(0);
                    }

                    // 处理每个活动项
                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();

                        // ContentHash 去重
                        using var dupCmd = conn.CreateCommand();
                        dupCmd.CommandText = "SELECT COUNT(1) FROM clip_items WHERE ContentHash=$h AND IsDeleted=0;";
                        dupCmd.Parameters.AddWithValue("$h", item.ContentHash);
                        if (Convert.ToInt64(dupCmd.ExecuteScalar()) > 0)
                        {
                            skipped++;
                            continue;
                        }

                        // 把受管路径列重链到当前数据根的实际落地位置（契约 §3.84）。
                        // 跨机器/跨数据根导入时导出机的绝对路径不在当前 Root 下，
                        // 必须按归档相对路径映射到 finalPath，否则图片/文件/缩略图断链。
                        // 外部引用（OriginalPath / 仅引用项）不在归档内 → 不匹配 → 保持原样。
                        if (!string.IsNullOrEmpty(item.RefPath))
                            item.RefPath = RelinkPayloadPath(item.RefPath, relToFinal);
                        if (!string.IsNullOrEmpty(item.ThumbnailPath))
                            item.ThumbnailPath = RelinkPayloadPath(item.ThumbnailPath, relToFinal);
                        if (!string.IsNullOrEmpty(item.SourceIconPath))
                            item.SourceIconPath = RelinkPayloadPath(item.SourceIconPath, relToFinal);
                        // FileDrop manifest: 重链受管 StoredPath（外部引用保持不变）
                        if (item.Type == ClipType.File && !string.IsNullOrEmpty(item.RefPath))
                        {
                            try
                            {
                                var fm = ClipFileManifest.Load(item.RefPath);
                                if (fm is not null)
                                {
                                    foreach (var entry in fm.Entries)
                                    {
                                        if (entry.StoredPath is not null)
                                            entry.StoredPath = RelinkPayloadPath(entry.StoredPath, relToFinal);
                                    }
                                    fm.Save(item.RefPath);
                                }
                            }
                            catch { /* 损坏的 manifest 跳过 */ }
                        }

                        // 插入新行
                        using var insCmd = conn.CreateCommand();
                        insCmd.CommandText = @"
                            INSERT INTO clip_items
                                (Type,PreviewText,TextContent,RefPath,ThumbnailPath,SourceApp,SourceIconPath,
                                 CreatedAt,IsPinned,ContentHash,SizeBytes,IsDeleted,DeletedAt,OcrText,OcrStatus)
                            VALUES
                                ($t,$p,$tc,$r,$th,$sa,$si,$ca,$ip,$ch,$sz,0,NULL,$ot,$os);";
                        insCmd.Parameters.AddWithValue("$t", (int)item.Type);
                        insCmd.Parameters.AddWithValue("$p", item.PreviewText ?? "");
                        insCmd.Parameters.AddWithValue("$tc", (object?)item.TextContent ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$r", (object?)item.RefPath ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$th", (object?)item.ThumbnailPath ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$sa", (object?)item.SourceApp ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$si", (object?)item.SourceIconPath ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$ca", IsoFormat(item.CreatedAt));
                        insCmd.Parameters.AddWithValue("$ip", item.IsPinned ? 1 : 0);
                        insCmd.Parameters.AddWithValue("$ch", item.ContentHash ?? "");
                        insCmd.Parameters.AddWithValue("$sz", item.SizeBytes);
                        insCmd.Parameters.AddWithValue("$ot", (object?)item.OcrText ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$os", (int)item.OcrStatus);
                        insCmd.ExecuteNonQuery();

                        using var idCmd = conn.CreateCommand();
                        idCmd.CommandText = "SELECT last_insert_rowid();";
                        long newId = Convert.ToInt64(idCmd.ExecuteScalar());

                        // 标签关联
                        foreach (var (clipItemId, tagId) in tagMappings)
                        {
                            if (clipItemId != item.Id) continue;
                            var tag = tags.FirstOrDefault(t => t.Id == tagId);
                            if (tag is null) continue;

                            if (!tagNameMap.TryGetValue(tag.Name, out long existingTagId))
                            {
                                using var newTagCmd = conn.CreateCommand();
                                newTagCmd.CommandText = "INSERT INTO tags (Name,Color,SortOrder) VALUES ($n,$c,$so);";
                                newTagCmd.Parameters.AddWithValue("$n", tag.Name);
                                newTagCmd.Parameters.AddWithValue("$c", tag.Color);
                                newTagCmd.Parameters.AddWithValue("$so", tag.SortOrder);
                                newTagCmd.ExecuteNonQuery();
                                existingTagId = Convert.ToInt64(new SqliteCommand(
                                    "SELECT last_insert_rowid();", conn).ExecuteScalar());
                                tagNameMap[tag.Name] = existingTagId;
                            }

                            using var relCmd = conn.CreateCommand();
                            relCmd.CommandText = "INSERT OR IGNORE INTO clip_item_tags (ClipItemId,TagId) VALUES ($c,$t);";
                            relCmd.Parameters.AddWithValue("$c", newId);
                            relCmd.Parameters.AddWithValue("$t", existingTagId);
                            relCmd.ExecuteNonQuery();
                        }

                        imported++;
                        itemIdx++;
                        progress?.Report(new(BackupPhase.Merging, itemIdx, manifest.ItemCount));
                    }

                    // 写哨兵
                    using var sentinelCmd = conn.CreateCommand();
                    sentinelCmd.CommandText = "INSERT INTO backup_import_batches (ImportId,CommittedAtUtc) VALUES ($id,$t);";
                    sentinelCmd.Parameters.AddWithValue("$id", importId.ToString("D"));
                    sentinelCmd.Parameters.AddWithValue("$t", IsoFormat(DateTime.UtcNow));
                    sentinelCmd.ExecuteNonQuery();

                    txn.Commit();
                }
                catch
                {
                    // ROLLBACK + 删除本批新文件
                    try { txn.Rollback(); } catch { }
                    foreach (var path in finalPaths)
                    {
                        try { File.Delete(path); } catch { }
                    }
                    throw;
                }
            }

            // 10. 成功后清理 journal + staging
            progress?.Report(new(BackupPhase.Finalizing, imported, manifest.ItemCount));
            if (journalPath is not null)
            {
                try { File.Delete(journalPath); } catch { }
            }
            if (stagingRoot is not null)
            {
                try { Directory.Delete(stagingRoot, true); } catch { }
            }

            return new BackupImportResult(true, imported, skipped, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 失败不删 journal（供崩溃恢复使用）
            return new BackupImportResult(false, 0, 0, ex.Message);
        }
    }

    // ══════════════════════════════════════════════
    //  内部 helper
    // ══════════════════════════════════════════════

    private static SqliteConnection OpenTempDb(string dbPath)
    {
        // 临时 DB 禁用连接池 + 使用 SqliteConnection.ClearPool 释放锁
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static (List<ClipItem> Items, List<Tag> Tags, List<(long ClipItemId, long TagId)> TagMappings)
        ReadActiveItemsFromSnapshot(string dbPath)
    {
        var items = new List<ClipItem>();
        var tags = new List<Tag>();
        var tagMappings = new List<(long, long)>();

        using var conn = OpenTempDb(dbPath);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM clip_items WHERE IsDeleted=0 ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add(SqliteClipStore.MapFrom(reader));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM tags ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tags.Add(new Tag
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Color = reader.GetString(2),
                    SortOrder = reader.GetInt32(3),
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ClipItemId, TagId FROM clip_item_tags;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                tagMappings.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return (items, tags, tagMappings);
    }

    private static void CollectPayloadPaths(ClipItem item, string root, HashSet<string> paths)
    {
        // 图片/富文本/来源图标的直接路径
        AddIfUnderRoot(item.RefPath, root, paths);
        AddIfUnderRoot(item.ThumbnailPath, root, paths);
        AddIfUnderRoot(item.SourceIconPath, root, paths);

        // 文件 manifest 中的受管路径
        if (item.Type == ClipType.File && !string.IsNullOrEmpty(item.RefPath))
        {
            try
            {
                var manifest = ClipFileManifest.Load(item.RefPath);
                if (manifest is not null && !manifest.IsReferenceOnly)
                {
                    foreach (var entry in manifest.Entries)
                    {
                        AddIfUnderRoot(entry.StoredPath, root, paths);
                    }
                }
                // manifest 本身也需要归档
                AddIfUnderRoot(item.RefPath, root, paths);
            }
            catch { /* 损坏的 manifest 不阻塞导出 */ }
        }
    }

    private static void AddIfUnderRoot(string? path, string root, HashSet<string> paths)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            string full = Path.GetFullPath(path);
            string normalizedRoot = Path.GetFullPath(root);
            if (full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && File.Exists(full))
                paths.Add(full);
        }
        catch { /* 非法路径跳过 */ }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static int GetCurrentSchemaVersion()
    {
        // Schema 版本 = clip_items 当前列集合的版本号
        // M5.1 增列后 = 2（初始 CREATE TABLE = 1，加 OcrText/OcrStatus = 2）
        return 2;
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
            return info.ProductVersion ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    // ══════════════════════════════════════════════
    //  导入 helper
    // ══════════════════════════════════════════════

    private string MakeUniqueFinalPath(string relativePath)
    {
        string candidate = Path.Combine(_paths.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(candidate))
            return candidate;

        string dir = Path.GetDirectoryName(candidate)!;
        string name = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        int idx = 2;
        while (true)
        {
            string alt = Path.Combine(dir, $"{name}_{idx}{ext}");
            if (!File.Exists(alt))
                return alt;
            idx++;
        }
    }

    /// <summary>
    /// 把归档内某条 DB 路径列重链到 payload 实际落地的 finalPath。
    /// 按归档相对路径（如 <c>images\foo.png</c>）做后缀匹配，因此与导出机的
    /// 数据根前缀无关，可正确支持异机/跨数据根恢复。未命中（外部引用/仅引用项）
    /// 一律保持原值。命中多条时取最长相对路径，避免歧义。
    /// </summary>
    internal static string RelinkPayloadPath(string dbPath, Dictionary<string, string> relToFinal)
    {
        if (string.IsNullOrEmpty(dbPath))
            return dbPath;
        try
        {
            string norm = dbPath.Replace('/', Path.DirectorySeparatorChar);
            string? bestKey = null;
            foreach (string key in relToFinal.Keys)
            {
                if (norm.EndsWith(Path.DirectorySeparatorChar + key, StringComparison.OrdinalIgnoreCase)
                    || norm.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    if (bestKey is null || key.Length > bestKey.Length)
                        bestKey = key;
                }
            }
            if (bestKey is not null)
                return relToFinal[bestKey];
        }
        catch { }
        return dbPath;
    }

    private static string IsoFormat(DateTime dt) =>
        dt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    internal static bool IsZipSlipForTest(string entryPath) =>
        IsZipSlip(entryPath);

    private static bool IsZipSlip(string entryPath)
    {
        string normalized = entryPath.Replace('/', Path.DirectorySeparatorChar);
        // 绝对/带盘符/UNC/驱动器相对路径：Path.Combine 会丢弃 staging 前缀，
        // 使解压逃逸 staging 目录，必须一律拒绝（仅 `..` 检查不足以防御）。
        if (Path.IsPathRooted(normalized))
            return true;
        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        return normalized.StartsWith("..") || normalized.Contains($"{Path.DirectorySeparatorChar}..");
    }
}

/// <summary>备份归档 manifest.json 的结构。</summary>
internal sealed class BackupManifest
{
    public int FormatVersion { get; set; }
    public int SchemaVersion { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int ItemCount { get; set; }
    public bool IncludesPayloads { get; set; }
    public List<BackupManifestEntry> Entries { get; set; } = new();
}

internal readonly record struct BackupManifestEntry(string RelativePath, long Length, string Sha256);

/// <summary>导入崩溃恢复 journal。</summary>
internal sealed class BackupImportJournal
{
    public string ImportId { get; set; } = string.Empty;
    public string StagingRoot { get; set; } = string.Empty;
    public List<string> FinalPaths { get; set; } = new();
    public string Phase { get; set; } = string.Empty;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BackupManifest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<BackupManifestEntry>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BackupImportJournal))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
