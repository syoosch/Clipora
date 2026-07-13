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
    private const int CurrentSchemaVersion = 2;
    private const int MaxArchiveEntries = 100_000;
    private const long MaxArchiveBytes = 10L * 1024 * 1024 * 1024;
    private const long MaxManifestBytes = 4L * 1024 * 1024;

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
                if (!BackupPathPolicy.TryGetManagedRelativePath(_paths.Root, absPath, out string relPath))
                    throw new InvalidDataException($"导出 payload 不在允许的受管目录：{Path.GetFileName(absPath)}");

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
            ValidatedArchive validated = ValidateArchiveLayout(zip, requireSupportedVersion: false, ct);
            BackupManifest manifest = validated.Manifest;
            bool compatible = manifest.FormatVersion == FormatVersion
                && manifest.SchemaVersion == CurrentSchemaVersion;
            string? incompatibility = compatible ? null
                : $"格式版本 {manifest.FormatVersion}/schema {manifest.SchemaVersion} 不被当前版本支持";

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
        var finalFiles = new List<(string RelativePath, string AbsolutePath)>();
        bool preserveStagingForRecovery = false;

        try
        {
            // 1. 归档布局/manifest 预检（不信任 Inspect 的 UI 结果，导入时重新完整验证）
            progress?.Report(new(BackupPhase.Preparing, 0, 0));
            using var zip = ZipFile.OpenRead(archivePath);
            ValidatedArchive validated = ValidateArchiveLayout(zip, requireSupportedVersion: true, ct);
            EnsureFreeSpace(_paths.Root, validated.TotalUncompressedBytes);
            int? sourceZoneId = TryReadInternetZoneId(archivePath);

            // 2. 创建相互隔离的 app-owned state 与 untrusted archive 目录
            stagingRoot = Path.Combine(_paths.Root, ".backup-import-staging", importId.ToString("D"));
            string stateRoot = Path.Combine(stagingRoot, "state");
            string archiveRoot = Path.Combine(stagingRoot, "archive");
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(archiveRoot);

            // 3. 逐流解压并同时核对实际长度、总展开量与 SHA-256。
            progress?.Report(new(BackupPhase.CopyingFiles, 0, zip.Entries.Count));
            int fileIdx = 0;
            long actualExpandedBytes = 0;
            foreach (ValidatedArchiveEntry validatedEntry in validated.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string destPath = BackupPathPolicy.CombineUnderRoot(archiveRoot, validatedEntry.NormalizedPath);
                if (validatedEntry.IsDirectory)
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }
                if (BackupPathPolicy.HasReparsePointInExistingAncestors(archiveRoot, destPath))
                    throw new InvalidDataException($"归档目标路径包含重解析点：{validatedEntry.NormalizedPath}");

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using Stream source = validatedEntry.Entry.Open();
                using var destination = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using IncrementalHash? hash = validatedEntry.ManifestEntry is null
                    ? null
                    : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                long entryBytes = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct)
                        .ConfigureAwait(false);
                    hash?.AppendData(buffer, 0, read);
                    entryBytes = checked(entryBytes + read);
                    actualExpandedBytes = checked(actualExpandedBytes + read);
                    if (entryBytes > validatedEntry.Entry.Length || actualExpandedBytes > MaxArchiveBytes)
                        throw new InvalidDataException("归档实际展开大小超过声明或硬上限");
                }

                if (entryBytes != validatedEntry.Entry.Length)
                    throw new InvalidDataException($"归档条目实际长度不符：{validatedEntry.NormalizedPath}");
                if (validatedEntry.ManifestEntry is BackupManifestEntry manifestEntry)
                {
                    if (entryBytes != manifestEntry.Length)
                        throw new InvalidDataException($"manifest 声明长度不符：{manifestEntry.RelativePath}");
                    string actualHash = Convert.ToHexString(hash!.GetHashAndReset());
                    if (!actualHash.Equals(manifestEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"SHA-256 校验失败：{manifestEntry.RelativePath}");
                }

                fileIdx++;
                progress?.Report(new(BackupPhase.CopyingFiles, fileIdx, zip.Entries.Count));
            }

            // 4. 恶意 SQLite / schema / 行语义 / payload 映射验证。
            progress?.Report(new(BackupPhase.Validating, 0, validated.Manifest.Entries.Count));
            string stagingDb = BackupPathPolicy.CombineUnderRoot(archiveRoot, "clipora.db");
            BackupDatabaseSnapshot snapshot = BackupDatabaseValidator.ValidateAndRead(
                stagingDb,
                validated.Manifest.SchemaVersion,
                validated.Manifest.ItemCount,
                archiveRoot,
                validated.PayloadRelativePaths);
            progress?.Report(new(BackupPhase.Validating, validated.Manifest.Entries.Count, validated.Manifest.Entries.Count));

            // 5. 为 payload 分配最终唯一受管相对路径（不覆盖现有文件）。
            //    同时记录「归档相对路径 → 实际落地 finalPath」映射，供 DB 路径列重链。
            var payloadMoves = new List<(string StagingPath, string FinalPath)>();
            var relToFinal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in validated.PayloadRelativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string stagingPath = BackupPathPolicy.CombineUnderRoot(archiveRoot, "payloads/" + relativePath);
                string finalRelative = MakeUniqueFinalRelativePath(relativePath);
                string finalPath = BackupPathPolicy.CombineUnderRoot(_paths.Root, finalRelative);
                if (BackupPathPolicy.HasReparsePointInExistingAncestors(_paths.Root, finalPath))
                    throw new InvalidDataException($"最终路径包含重解析点：{finalRelative}");
                payloadMoves.Add((stagingPath, finalPath));
                finalFiles.Add((finalRelative, finalPath));
                string relKey = relativePath.Replace('/', Path.DirectorySeparatorChar);
                relToFinal[relKey] = finalPath;
            }

            // 6. journal v2 仅记录受管相对路径，原子写入且先于任何 payload 移动。
            journalPath = Path.Combine(stateRoot, "import-journal.json");
            var journal = new BackupImportJournal
            {
                Version = 2,
                ImportId = importId.ToString("D"),
                FinalRelativePaths = finalFiles.Select(file => file.RelativePath).ToList(),
                Phase = "pre_commit",
            };
            WriteJournalAtomically(journalPath, journal);

            // 7. 合并式去重导入
            progress?.Report(new(BackupPhase.Merging, 0, validated.Manifest.ItemCount));
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
                        TryWriteInternetZone(finalPath, sourceZoneId);
                    }

                    List<ClipItem> items = snapshot.Items;
                    List<Tag> tags = snapshot.Tags;
                    List<(long ClipItemId, long TagId)> tagMappings = snapshot.TagMappings;

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
                        progress?.Report(new(BackupPhase.Merging, itemIdx, validated.Manifest.ItemCount));
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
                    preserveStagingForRecovery = !TryDeleteFinalFiles(finalFiles);
                    throw;
                }
            }

            // 8. 成功后清理 journal + staging；清理失败由下次启动按 sentinel 收尾。
            progress?.Report(new(BackupPhase.Finalizing, imported, validated.Manifest.ItemCount));
            if (journalPath is not null)
            {
                try { File.Delete(journalPath); } catch { }
            }
            if (stagingRoot is not null)
            {
                BackupPathPolicy.SafeDeleteTree(Path.Combine(_paths.Root, ".backup-import-staging"), stagingRoot);
            }

            return new BackupImportResult(true, imported, skipped, null);
        }
        catch (OperationCanceledException)
        {
            preserveStagingForRecovery = !TryDeleteFinalFiles(finalFiles);
            throw;
        }
        catch (Exception ex)
        {
            preserveStagingForRecovery = !TryDeleteFinalFiles(finalFiles);
            return new BackupImportResult(false, 0, 0, ex.Message);
        }
        finally
        {
            if (!preserveStagingForRecovery && stagingRoot is not null)
                BackupPathPolicy.SafeDeleteTree(Path.Combine(_paths.Root, ".backup-import-staging"), stagingRoot);
        }
    }

    // ══════════════════════════════════════════════
    //  内部 helper
    // ══════════════════════════════════════════════

    private static ValidatedArchive ValidateArchiveLayout(
        ZipArchive zip, bool requireSupportedVersion, CancellationToken ct)
    {
        if (zip.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException($"归档条目数 {zip.Entries.Count} 超过上限 {MaxArchiveEntries}");

        var normalizedEntries = new Dictionary<string, ValidatedArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            bool isDirectory = string.IsNullOrEmpty(entry.Name)
                && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'));
            if (!BackupPathPolicy.TryNormalizeArchiveEntry(
                    entry.FullName, isDirectory, out string normalized, out string error))
                throw new InvalidDataException($"归档路径被拒绝：{entry.FullName}（{error}）");
            if (!normalizedEntries.TryAdd(normalized, new ValidatedArchiveEntry(entry, normalized, isDirectory)))
                throw new InvalidDataException($"归档存在规范化后重复路径：{normalized}");
            if (isDirectory && entry.Length != 0)
                throw new InvalidDataException($"归档目录条目长度非零：{normalized}");

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaxArchiveBytes)
                throw new InvalidDataException("归档展开大小超过 10GB 上限");
        }

        if (!normalizedEntries.TryGetValue("manifest.json", out ValidatedArchiveEntry? manifestArchiveEntry)
            || manifestArchiveEntry.IsDirectory)
            throw new InvalidDataException("归档必须恰好包含一个 manifest.json");
        if (manifestArchiveEntry.Entry.Length > MaxManifestBytes)
            throw new InvalidDataException("manifest.json 超过 4 MiB 上限");

        BackupManifest manifest;
        using (Stream stream = manifestArchiveEntry.Entry.Open())
        using (var memory = new MemoryStream())
        {
            byte[] buffer = new byte[64 * 1024];
            long bytes = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                bytes = checked(bytes + read);
                if (bytes > MaxManifestBytes || bytes > manifestArchiveEntry.Entry.Length)
                    throw new InvalidDataException("manifest.json 实际展开大小异常");
                memory.Write(buffer, 0, read);
            }
            if (bytes != manifestArchiveEntry.Entry.Length)
                throw new InvalidDataException("manifest.json 实际长度与 ZIP 声明不一致");
            memory.Position = 0;
            manifest = JsonSerializer.Deserialize(memory, JsonContext.Default.BackupManifest)
                ?? throw new InvalidDataException("manifest.json 无效");
        }

        if (manifest.FormatVersion <= 0
            || (requireSupportedVersion && manifest.FormatVersion != FormatVersion))
            throw new InvalidDataException($"不支持的备份格式版本 {manifest.FormatVersion}");
        if (manifest.ItemCount < 0)
            throw new InvalidDataException("manifest ItemCount 不能为负数");
        if (!manifest.IncludesPayloads || manifest.Entries is null)
            throw new InvalidDataException("manifest 缺少 payload 声明");

        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedArchiveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        var payloadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sawDatabase = false;
        foreach (BackupManifestEntry manifestEntry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (manifestEntry.Length < 0
                || manifestEntry.Sha256.Length != 64
                || !manifestEntry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"manifest 长度或 SHA-256 非法：{manifestEntry.RelativePath}");

            string normalizedRelative;
            string archivePath;
            if (manifestEntry.RelativePath.Equals("clipora.db", StringComparison.OrdinalIgnoreCase))
            {
                normalizedRelative = "clipora.db";
                archivePath = "clipora.db";
                if (sawDatabase)
                    throw new InvalidDataException("manifest 重复声明 clipora.db");
                sawDatabase = true;
            }
            else
            {
                if (!BackupPathPolicy.TryNormalizeManagedRelativePath(
                        manifestEntry.RelativePath, out normalizedRelative, out string error))
                    throw new InvalidDataException($"manifest 路径被拒绝：{manifestEntry.RelativePath}（{error}）");
                archivePath = "payloads/" + normalizedRelative;
                payloadPaths.Add(normalizedRelative);
            }

            if (!manifestPaths.Add(normalizedRelative))
                throw new InvalidDataException($"manifest 存在规范化后重复路径：{normalizedRelative}");
            expectedArchiveFiles.Add(archivePath);
            if (!normalizedEntries.TryGetValue(archivePath, out ValidatedArchiveEntry? archiveEntry)
                || archiveEntry.IsDirectory)
                throw new InvalidDataException($"归档缺少 manifest 声明的条目：{archivePath}");
            if (archiveEntry.Entry.Length != manifestEntry.Length)
                throw new InvalidDataException($"ZIP 与 manifest 声明长度不一致：{normalizedRelative}");
            archiveEntry.ManifestEntry = manifestEntry with { RelativePath = normalizedRelative };
        }

        if (!sawDatabase)
            throw new InvalidDataException("manifest 必须恰好声明一个 clipora.db");
        foreach (ValidatedArchiveEntry entry in normalizedEntries.Values.Where(entry => !entry.IsDirectory))
        {
            if (!expectedArchiveFiles.Contains(entry.NormalizedPath))
                throw new InvalidDataException($"归档包含未声明条目：{entry.NormalizedPath}");
        }

        return new ValidatedArchive(
            manifest,
            normalizedEntries.Values.ToList(),
            payloadPaths,
            totalBytes);
    }

    private static void EnsureFreeSpace(string root, long expandedBytes)
    {
        string fullRoot = Path.GetFullPath(root);
        string driveRoot = Path.GetPathRoot(fullRoot)
            ?? throw new InvalidDataException("无法确定目标磁盘");
        var drive = new DriveInfo(driveRoot);
        long reserve = Math.Max(512L * 1024 * 1024, checked((expandedBytes + 9) / 10));
        long required = checked(expandedBytes + reserve);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"目标磁盘空间不足：至少需要 {required} 字节可用空间");
    }

    private static void WriteJournalAtomically(string journalPath, BackupImportJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        string tempPath = journalPath + ".tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(journal, JsonContext.Default.BackupImportJournal);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json, 0, json.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, journalPath, overwrite: false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private bool TryDeleteFinalFiles(IEnumerable<(string RelativePath, string AbsolutePath)> files)
    {
        bool allDeleted = true;
        foreach ((string relativePath, string absolutePath) in files)
        {
            try
            {
                if (!BackupPathPolicy.TryNormalizeManagedRelativePath(relativePath, out string normalized, out _))
                {
                    allDeleted = false;
                    continue;
                }
                string validatedPath = BackupPathPolicy.CombineUnderRoot(_paths.Root, normalized);
                if (!validatedPath.Equals(Path.GetFullPath(absolutePath), StringComparison.OrdinalIgnoreCase))
                {
                    allDeleted = false;
                    continue;
                }
                File.Delete(validatedPath);
                if (File.Exists(validatedPath))
                    allDeleted = false;
            }
            catch
            {
                allDeleted = false;
            }
        }
        return allDeleted;
    }

    private static int? TryReadInternetZoneId(string archivePath)
    {
        try
        {
            string text = File.ReadAllText(archivePath + ":Zone.Identifier", Encoding.UTF8);
            foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["ZoneId=".Length..], out int zoneId)
                    && zoneId is 3 or 4)
                    return zoneId;
            }
        }
        catch { }
        return null;
    }

    private static void TryWriteInternetZone(string filePath, int? zoneId)
    {
        if (zoneId is not (3 or 4))
            return;
        try
        {
            File.WriteAllText(
                filePath + ":Zone.Identifier",
                $"[ZoneTransfer]\r\nZoneId={zoneId.Value}\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // FAT/exFAT 等不支持 ADS；MOTW 是 best-effort，不阻断合法导入。
        }
    }

    internal static void TryWriteInternetZoneForTest(string filePath, int? zoneId)
        => TryWriteInternetZone(filePath, zoneId);

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
            if (BackupPathPolicy.TryGetManagedRelativePath(root, full, out _)
                && !BackupPathPolicy.HasReparsePointInExistingAncestors(root, full)
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
        return CurrentSchemaVersion;
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

    private string MakeUniqueFinalRelativePath(string relativePath)
    {
        if (!BackupPathPolicy.TryNormalizeManagedRelativePath(relativePath, out string normalized, out string error))
            throw new InvalidDataException($"最终受管路径无效：{error}");

        string candidate = BackupPathPolicy.CombineUnderRoot(_paths.Root, normalized);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return normalized;

        string normalizedDir = Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar))!
            .Replace('\\', '/');
        string name = Path.GetFileNameWithoutExtension(normalized);
        string ext = Path.GetExtension(normalized);
        int idx = 2;
        while (true)
        {
            string altRelative = normalizedDir + "/" + $"{name}_{idx}{ext}";
            string alt = BackupPathPolicy.CombineUnderRoot(_paths.Root, altRelative);
            if (!File.Exists(alt) && !Directory.Exists(alt))
                return altRelative;
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
        !BackupPathPolicy.TryNormalizeArchiveEntry(entryPath, isDirectory: false, out _, out _);

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
    public int Version { get; set; } = 1;
    public string ImportId { get; set; } = string.Empty;
    public List<string> FinalRelativePaths { get; set; } = new();
    // 仅用于读取 v1 journal；v2 writer 永不填充以下绝对路径字段。
    public string? StagingRoot { get; set; }
    public List<string>? FinalPaths { get; set; }
    public string Phase { get; set; } = string.Empty;
}

internal sealed record ValidatedArchive(
    BackupManifest Manifest,
    List<ValidatedArchiveEntry> Entries,
    HashSet<string> PayloadRelativePaths,
    long TotalUncompressedBytes);

internal sealed class ValidatedArchiveEntry(
    ZipArchiveEntry entry,
    string normalizedPath,
    bool isDirectory)
{
    internal ZipArchiveEntry Entry { get; } = entry;
    internal string NormalizedPath { get; } = normalizedPath;
    internal bool IsDirectory { get; } = isDirectory;
    internal BackupManifestEntry? ManifestEntry { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BackupManifest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<BackupManifestEntry>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BackupImportJournal))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
