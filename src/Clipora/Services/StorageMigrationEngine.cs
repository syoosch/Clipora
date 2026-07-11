using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipora.Abstractions;
using Clipora.Models;
using Microsoft.Data.Sqlite;

namespace Clipora.Services;

/// <summary>存储迁移纯引擎：验证 → checkpoint → 复制 → 重写 → 验证 → 提升 → 切换。</summary>
internal sealed class StorageMigrationEngine : IStorageMigrationEngine
{
    private const string DbFileName = "clipora.db";
    private const string SettingsFileName = "settings.json";
    private static readonly string[] ExcludedFiles = { "clipora.db-wal", "clipora.db-shm" };

    private readonly IStorageMigrationStateStore _stateStore;
    private readonly IStorageMigrationFaultInjector _faultInjector;
    private readonly ISpaceProbe _spaceProbe;

    public StorageMigrationEngine(
        IStorageMigrationStateStore stateStore,
        IStorageMigrationFaultInjector? faultInjector = null,
        ISpaceProbe? spaceProbe = null)
    {
        _stateStore = stateStore;
        _faultInjector = faultInjector ?? new NoOpFaultInjector();
        _spaceProbe = spaceProbe ?? new DefaultSpaceProbe();
    }

    public StorageMigrationResult Execute(
        StorageMigrationRequest request,
        IProgress<StorageMigrationProgress>? progress = null)
    {
        try
        {
            return ExecuteInternal(request, progress);
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Failed, StorageMigrationError.Unknown,
                request.SourceRoot, false, $"未处理异常: {ex.Message}");
        }
    }

    private StorageMigrationResult ExecuteInternal(
        StorageMigrationRequest request,
        IProgress<StorageMigrationProgress>? progress)
    {
        // —— 0. 参数基本校验 ——
        if (request is null)
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidRequest, string.Empty, false, "请求为 null");

        if (!Path.IsPathFullyQualified(request.SourceRoot) || !Path.IsPathFullyQualified(request.TargetRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidRequest, request.SourceRoot, false, "source/target 必须为完全限定路径");

        string sourceRoot;
        string targetRoot;
        try
        {
            sourceRoot = Path.GetFullPath(request.SourceRoot);
            targetRoot = Path.GetFullPath(request.TargetRoot);
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidRequest, request.SourceRoot, false, $"路径规范化失败: {ex.Message}");
        }

        Guid migrationId = request.MigrationId;

        // —— 1. 状态读入 ——
        StorageMigrationState state;
        try
        {
            state = _stateStore.Read();
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, $"无法读取状态: {ex.Message}");
        }

        string activeRoot;
        try { activeRoot = Path.GetFullPath(state.ActiveRoot); }
        catch { return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "ActiveRoot 无效"); }

        // —— 2. 状态检查与恢复路径 ——
        // 2a. active 已是 target + pending/id 残留 → 只验证并清理
        if (string.Equals(activeRoot, targetRoot, StringComparison.OrdinalIgnoreCase)
            && state.PendingRoot is not null && state.MigrationId == migrationId)
        {
            var targetMarker = StorageMigrationMarker.Read(targetRoot);
            if (targetMarker is null || targetMarker.SchemaVersion != 1
                || !Guid.TryParse(targetMarker.MigrationId, out Guid tmId) || tmId != migrationId)
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, activeRoot, false, "active==target 但 marker 不匹配");

            var verifyResult = VerifyTarget(targetRoot, sourceRoot, targetRoot, migrationId, progress);
            if (!verifyResult.Succeeded)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, activeRoot, false, $"target 验证失败: {verifyResult.Detail}");

            try { _stateStore.ClearPending(migrationId); }
            catch (Exception ex)
            { return Fail(StorageMigrationPhase.Switching, StorageMigrationError.SwitchFailed, activeRoot, false, $"清理 pending 失败: {ex.Message}"); }

            // 标记完成（与 SwitchAndCommit 一致：失败不影响已提交的 state）
            try
            {
                StorageMigrationMarker.UpdatePhase(targetRoot, StorageMigrationPhase.Completed,
                    new StorageMigrationMarkerData
                    {
                        SchemaVersion = 1, MigrationId = migrationId.ToString(),
                        SourceRoot = sourceRoot, TargetRoot = targetRoot,
                        CreatedAtUtc = targetMarker.CreatedAtUtc,
                        Phase = StorageMigrationPhase.Switching.ToString(),
                    });
            }
            catch { /* marker 写入失败不影响已提交的结果 */ }

            Report(progress, StorageMigrationPhase.Completed, 0, 0, 0, 0);
            return Ok(StorageMigrationPhase.Completed, true, activeRoot, "状态已一致：target 已激活，pending 已清理");
        }

        // 状态不匹配
        if (!string.Equals(activeRoot, sourceRoot, StringComparison.OrdinalIgnoreCase))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, activeRoot, false,
                $"ActiveRoot 与 source 不一致（active={activeRoot}, source={sourceRoot}）");

        // 2b. active==source + pending/id 匹配 + final target 已存在 → 验证并只重试 commit
        bool finalTargetExists = Directory.Exists(targetRoot);
        if (finalTargetExists)
        {
            if (state.PendingRoot is null || state.MigrationId != migrationId)
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "target 已存在但无匹配的 pending 状态");

            string? pendingFull;
            try { pendingFull = Path.GetFullPath(state.PendingRoot); }
            catch { pendingFull = null; }

            if (!string.Equals(pendingFull, targetRoot, StringComparison.OrdinalIgnoreCase))
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "pending 与 target 不一致");

            if (!StorageMigrationMarker.IsAppOwned(targetRoot, migrationId, sourceRoot, targetRoot, out var ftMarker))
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "target 已存在但 marker 不匹配");

            // PoD: target was promoted in a previous run
            var verifyFtResult = VerifyTarget(targetRoot, sourceRoot, targetRoot, migrationId, progress);
            if (!verifyFtResult.Succeeded)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"target 验证失败: {verifyFtResult.Detail}");

            return SwitchAndCommit(sourceRoot, targetRoot, migrationId, progress, /* promoted */ true);
        }

        // 2c. staging 存在 → 按 marker 处理
        string staging = GetStagingPath(targetRoot, migrationId);
        bool stagingExists = Directory.Exists(staging);
        if (stagingExists)
        {
            if (state.PendingRoot is null || state.MigrationId != migrationId)
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "staging 存在但无匹配的 pending 状态");

            if (StorageMigrationMarker.IsAppOwned(staging, migrationId, sourceRoot, targetRoot, out _))
            {
                // app-owned staging → 可安全清理后重新开始
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex)
                { return Fail(StorageMigrationPhase.Validating, StorageMigrationError.Unknown, sourceRoot, false, $"清理旧 staging 失败: {ex.Message}"); }
            }
            else
            {
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "staging 存在但 marker 不匹配，不可删除");
            }
        }

        // 2d. pending/id 不匹配
        if (state.PendingRoot is not null && state.MigrationId != migrationId)
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.StateMismatch, sourceRoot, false, "既存 pending 与当前 MigrationId 不匹配");

        // —— 3. 前置校验 ——
        var validateResult = Validate(sourceRoot, targetRoot, staging, migrationId);
        if (!validateResult.Succeeded)
        {
            try { _stateStore.ClearPending(migrationId); } catch { /* ignore */ }
            return validateResult;
        }

        Report(progress, StorageMigrationPhase.Validating, 0, 0, 0, 0);

        // —— 4-10. 主流程（try/catch 包裹故障注入以路由到 CleanupFail，验证 §10 矩阵） ——
        StorageMigrationPhase currentPhase = StorageMigrationPhase.Validating;
        try
        {
        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.BeforeCheckpoint);

        // —— 4. Checkpointing ——
        currentPhase = StorageMigrationPhase.Checkpointing;
        string sourceDbPath = Path.Combine(sourceRoot, DbFileName);
        var cpResult = Checkpoint(sourceRoot, sourceDbPath);
        if (!cpResult.Succeeded) return cpResult;

        Report(progress, StorageMigrationPhase.Checkpointing, 0, 0, 0, 0);

        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.AfterCheckpoint);

        // —— 5. 获取文件清单 ——
        var fileEntries = EnumerateSourceFiles(sourceRoot);
        var copyItems = fileEntries.Where(f => !ShouldExcludeSourceFile(sourceRoot, f)).ToList();

        long totalBytes = copyItems.Sum(f => new FileInfo(f).Length);
        int totalFiles = copyItems.Count;

        // —— 6. 创建 staging + marker ——
        try { Directory.CreateDirectory(staging); }
        catch (Exception ex)
        { return CleanupFail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, staging, migrationId, $"无法创建 staging: {ex.Message}"); }

        string stagingDbPath = Path.Combine(staging, DbFileName);

        var markerData = new StorageMigrationMarkerData
        {
            SchemaVersion = 1,
            MigrationId = migrationId.ToString(),
            SourceRoot = sourceRoot,
            TargetRoot = targetRoot,
            CreatedAtUtc = DateTime.UtcNow,
            Phase = StorageMigrationPhase.Checkpointing.ToString(),
        };
        try { StorageMigrationMarker.Write(staging, markerData); }
        catch (Exception ex)
        { return CleanupFail(StorageMigrationPhase.Checkpointing, StorageMigrationError.Unknown, sourceRoot, false, staging, migrationId, $"marker 写入失败: {ex.Message}"); }

        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.AfterMarkerCreated);

        // —— 7. Copying ——
        copyItems = copyItems
            .OrderByDescending(f => string.Equals(Path.GetFileName(f), DbFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        currentPhase = StorageMigrationPhase.Copying;
        var copyResult = CopyFiles(copyItems, sourceRoot, staging, totalFiles, totalBytes, progress);
        if (!copyResult.Succeeded)
            return CleanupFail(StorageMigrationPhase.Copying, copyResult.Error, sourceRoot, false, staging, migrationId, copyResult.Detail);

        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.AfterCopy);

        // —— 8. Rebasing ——
        currentPhase = StorageMigrationPhase.Rebasing;
        StorageMigrationMarker.UpdatePhase(staging, StorageMigrationPhase.Rebasing, markerData);

        var rebaseResult = Rebase(staging, stagingDbPath, sourceRoot, targetRoot, fileEntries);
        if (!rebaseResult.Succeeded)
            return CleanupFail(StorageMigrationPhase.Rebasing, rebaseResult.Error, sourceRoot, false, staging, migrationId, rebaseResult.Detail);

        Report(progress, StorageMigrationPhase.Rebasing, totalFiles, totalFiles, totalBytes, totalBytes);

        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.AfterRebase);

        // —— 9. Verifying ——
        currentPhase = StorageMigrationPhase.Verifying;
        StorageMigrationMarker.UpdatePhase(staging, StorageMigrationPhase.Verifying, markerData);

        var verifyResult2 = VerifyAll(sourceRoot, staging, targetRoot, copyItems, sourceDbPath, stagingDbPath, markerData, migrationId);
        if (!verifyResult2.Succeeded)
            return CleanupFail(StorageMigrationPhase.Verifying, verifyResult2.Error, sourceRoot, false, staging, migrationId, verifyResult2.Detail);

        Report(progress, StorageMigrationPhase.Verifying, totalFiles, totalFiles, totalBytes, totalBytes);

        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.AfterVerify);

        // —— 10. Promoting ——
        currentPhase = StorageMigrationPhase.Promoting;
        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.BeforePromote);

        SqliteConnection.ClearAllPools(); // 释放所有池化句柄，确保 staging db 可 Move/验证

        if (Directory.Exists(targetRoot))
            return CleanupFail(StorageMigrationPhase.Promoting, StorageMigrationError.PromotionFailed, sourceRoot, false, staging, migrationId, "target 在提升前出现（竞态）");

        StorageMigrationMarker.UpdatePhase(staging, StorageMigrationPhase.Promoting, markerData);

        try { Directory.Move(staging, targetRoot); }
        catch (Exception ex)
        { return CleanupFail(StorageMigrationPhase.Promoting, StorageMigrationError.PromotionFailed, sourceRoot, false, staging, migrationId, $"Directory.Move 失败: {ex.Message}"); }

        // promote 已成功：切到 Switching 阶段。此后任何故障都必须保留 target + pending（§10），
        // 绝不删除 target；currentPhase>=Switching 让 CleanupFail 走 promote-后分支。
        currentPhase = StorageMigrationPhase.Switching;

        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.AfterPromoteBeforeSwitch);

        // —— 11. Switching ——
        return SwitchAndCommit(sourceRoot, targetRoot, migrationId, progress, /* promoted */ true);

        } // end main-phase try
        catch (Exception ex)
        {
            // 故障注入或意外异常 → 路由到 CleanupFail 以执行 §10 清理矩阵。
            // promote 成功后（currentPhase>=Switching）必须保留 target+pending 并标记 promoted。
            bool promotedNow = currentPhase >= StorageMigrationPhase.Switching;
            return CleanupFail(currentPhase, StorageMigrationError.Unknown, sourceRoot, promotedNow, staging, migrationId, $"故障注入: {ex.Message}");
        }

        }

    // ─── Phase implementations ───

    private StorageMigrationResult Validate(string sourceRoot, string targetRoot, string staging, Guid migrationId)
    {
        // source 必须存在且包含 clipora.db
        if (!Directory.Exists(sourceRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidSource, sourceRoot, false, "source 目录不存在");
        if (!File.Exists(Path.Combine(sourceRoot, DbFileName)))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidSource, sourceRoot, false, "source 目录缺少 clipora.db");

        // target 必须不存在
        if (Directory.Exists(targetRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, "target 目录已存在");
        if (File.Exists(targetRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, "target 路径被文件占用");

        // target parent 必须存在且可写
        string? targetParent = Path.GetDirectoryName(targetRoot);
        if (string.IsNullOrEmpty(targetParent) || !Directory.Exists(targetParent))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, "target 父目录不存在");

        // 写权限探针
        string probePath = Path.Combine(targetParent, ".clipora-write-probe-" + migrationId.ToString("N"));
        try
        {
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
        }
        catch (Exception ex)
        { return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, $"target 父目录不可写: {ex.Message}"); }

        // 互不为父子
        if (AreRelated(sourceRoot, targetRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidRequest, sourceRoot, false, "source/target 互为父子目录");

        // UNC/网络路径拒绝
        if (IsNetworkPath(sourceRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidSource, sourceRoot, false, "source 为网络路径");
        if (IsNetworkPath(targetRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, "target 为网络路径");
        if (IsNetworkPath(staging))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, "staging 为网络路径");

        // source/target/staging parent 不得是 reparse point
        if (IsReparsePoint(sourceRoot))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidSource, sourceRoot, false, "source 是 reparse point");
        if (IsReparsePoint(targetParent))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidTarget, sourceRoot, false, "target parent 是 reparse point");

        // source 树中不得有 reparse point
        try
        {
            var reparse = FindReparsePoints(sourceRoot);
            if (reparse is not null)
                return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidSource, sourceRoot, false, $"source 树含 reparse point: {reparse}");
        }
        catch (Exception ex)
        { return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InvalidSource, sourceRoot, false, $"reparse 扫描失败: {ex.Message}"); }

        // 空间检查
        long sourceSize = ComputeSourceSize(sourceRoot);
        long requiredBytes = sourceSize + Math.Max(64L * 1024 * 1024, (long)(sourceSize * 0.1));

        if (!_spaceProbe.HasSufficientSpace(targetParent, requiredBytes))
            return Fail(StorageMigrationPhase.Validating, StorageMigrationError.InsufficientSpace, sourceRoot, false,
                $"空间不足：需要 {requiredBytes} bytes，source {sourceSize} bytes");

        return Ok(StorageMigrationPhase.Validating, false, sourceRoot, null);
    }

    private StorageMigrationResult Checkpoint(string sourceRoot, string sourceDbPath)
    {
        string cs = new SqliteConnectionStringBuilder { DataSource = sourceDbPath, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                int busy = reader.GetInt32(0);
                if (busy != 0)
                    return Fail(StorageMigrationPhase.Checkpointing, StorageMigrationError.CheckpointFailed, sourceRoot, false, $"WAL checkpoint busy={busy}");
            }
            connection.Close();
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Checkpointing, StorageMigrationError.CheckpointFailed, sourceRoot, false, $"checkpoint 失败: {ex.Message}");
        }

        // 确保 WAL/SHM 被清理
        try { File.Delete(Path.Combine(sourceRoot, "clipora.db-wal")); } catch { /* ignore */ }
        try { File.Delete(Path.Combine(sourceRoot, "clipora.db-shm")); } catch { /* ignore */ }

        return Ok(StorageMigrationPhase.Checkpointing, false, sourceRoot, null);
    }

    private StorageMigrationResult CopyFiles(
        List<string> files, string sourceRoot, string staging,
        int totalFiles, long totalBytes, IProgress<StorageMigrationProgress>? progress)
    {
        int completed = 0;
        long copiedBytes = 0;

        bool firstCopied = false;
        foreach (string sourceFile in files)
        {
            string relativePath = sourceFile[(sourceRoot.Length + 1)..];
            if (relativePath.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                relativePath = relativePath[1..];

            string destFile = Path.Combine(staging, relativePath);
            string? destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            try
            {
                using (var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(destFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    src.CopyTo(dst);
                }

                // 逐文件 hash 验证
                long srcLen = new FileInfo(sourceFile).Length;
                long dstLen = new FileInfo(destFile).Length;
                if (srcLen != dstLen)
                    return Fail(StorageMigrationPhase.Copying, StorageMigrationError.CopyFailed, sourceFile, false, $"复制后长度不一致: {srcLen} vs {dstLen}");

                byte[] srcHash, dstHash;
                using (var srcStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    srcHash = SHA256.HashData(srcStream);
                using (var dstStream = new FileStream(destFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    dstHash = SHA256.HashData(dstStream);
                if (!srcHash.SequenceEqual(dstHash))
                    return Fail(StorageMigrationPhase.Copying, StorageMigrationError.CopyFailed, sourceFile, false, "复制后 SHA-256 不一致");
            }
            catch (Exception ex)
            {
                return Fail(StorageMigrationPhase.Copying, StorageMigrationError.CopyFailed, sourceFile, false, $"复制失败: {ex.Message}");
            }

            completed++;
            copiedBytes += new FileInfo(sourceFile).Length;

            // §11: DuringCopy 在至少复制一个文件后触发
            if (!firstCopied)
            {
                firstCopied = true;
                _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.DuringCopy);
            }

            Report(progress, StorageMigrationPhase.Copying, completed, totalFiles, copiedBytes, totalBytes);
        }

        return Ok(StorageMigrationPhase.Copying, false, sourceRoot, null);
    }

    private StorageMigrationResult Rebase(string staging, string stagingDbPath, string sourceRoot, string targetRoot, List<string> sourceFiles)
    {
        // DB 路径重写
        var dbRewrite = StoragePathRewriter.RewriteDatabase(stagingDbPath, sourceRoot, targetRoot);
        if (!dbRewrite.Success)
            return Fail(StorageMigrationPhase.Rebasing, dbRewrite.Error, sourceRoot, false, dbRewrite.Message);

        // 所有 manifest 路径重写
        var manifestFiles = sourceFiles
            .Where(f => f.EndsWith(".clipora-files.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (string sourceManifest in manifestFiles)
        {
            string relativeManifest = sourceManifest[(sourceRoot.Length + 1)..];
            if (relativeManifest.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                relativeManifest = relativeManifest[1..];
            string stagingManifest = Path.Combine(staging, relativeManifest);

            if (File.Exists(stagingManifest))
            {
                var manifestRewrite = StoragePathRewriter.RewriteManifest(stagingManifest, sourceRoot, targetRoot);
                if (!manifestRewrite.Success)
                    return Fail(StorageMigrationPhase.Rebasing, manifestRewrite.Error, sourceRoot, false, manifestRewrite.Message);
            }
        }

        return Ok(StorageMigrationPhase.Rebasing, false, sourceRoot, null);
    }

    private StorageMigrationResult VerifyAll(
        string sourceRoot, string staging, string targetRoot,
        List<string> sourceFiles, string sourceDbPath, string stagingDbPath,
        StorageMigrationMarkerData markerData, Guid migrationId)
    {
        // 1. staging DB integrity
        string stagingCs = new SqliteConnectionStringBuilder { DataSource = stagingDbPath, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(stagingCs);
            connection.Open();

            // integrity_check
            using var icCmd = connection.CreateCommand();
            icCmd.CommandText = "PRAGMA integrity_check;";
            var icResult = icCmd.ExecuteScalar() as string;
            if (!string.Equals(icResult?.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"integrity_check: {icResult}");

            // foreign_key_check
            using var fkCmd = connection.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_key_check;";
            int fkCount = 0;
            using (var fkReader = fkCmd.ExecuteReader())
            { while (fkReader.Read()) fkCount++; }
            if (fkCount != 0)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"foreign_key_check: {fkCount} 行违规");

            // 2. clip_items 全列结构化比较（§7.3）
            long sourceCount = CountTable(sourceDbPath, "clip_items");
            long stagingCount = CountTable(stagingDbPath, "clip_items");
            if (sourceCount != stagingCount)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"clip_items 行数不一致: {sourceCount} vs {stagingCount}");

            var sourceClipRows = ReadAllClipItems(sourceDbPath);
            var stagingClipRows = ReadAllClipItems(stagingDbPath);
            var stagingById = stagingClipRows.ToDictionary(r => r.Id);

            foreach (var src in sourceClipRows)
            {
                if (!stagingById.TryGetValue(src.Id, out var stg))
                    return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"staging 缺失行 Id={src.Id}");

                // 非路径字段逐值相同
                if (src.Type != stg.Type) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} Type 不匹配");
                if (src.PreviewText != stg.PreviewText) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} PreviewText 不匹配");
                if (src.TextContent != stg.TextContent) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} TextContent 不匹配");
                if (src.SourceApp != stg.SourceApp) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} SourceApp 不匹配");
                if (src.CreatedAt != stg.CreatedAt) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} CreatedAt 不匹配");
                if (src.IsPinned != stg.IsPinned) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} IsPinned 不匹配");
                if (src.ContentHash != stg.ContentHash) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} ContentHash 不匹配");
                if (src.SizeBytes != stg.SizeBytes) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} SizeBytes 不匹配");
                if (src.IsDeleted != stg.IsDeleted) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} IsDeleted 不匹配");
                if (src.DeletedAt != stg.DeletedAt) return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} DeletedAt 不匹配");

                // 路径字段：按 §6 计算预期值
                string? expectedRef = StoragePathRewriter.RewriteManagedPath(src.RefPath, sourceRoot, targetRoot, "RefPath");
                string? expectedThumb = StoragePathRewriter.RewriteManagedPath(src.ThumbnailPath, sourceRoot, targetRoot, "ThumbnailPath");
                string? expectedIcon = StoragePathRewriter.RewriteManagedPath(src.SourceIconPath, sourceRoot, targetRoot, "SourceIconPath");

                if (!string.Equals(expectedRef, stg.RefPath, StringComparison.OrdinalIgnoreCase))
                    return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} RefPath 不匹配（期望={expectedRef}，实际={stg.RefPath}）");
                if (!string.Equals(expectedThumb, stg.ThumbnailPath, StringComparison.OrdinalIgnoreCase))
                    return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} ThumbnailPath 不匹配");
                if (!string.Equals(expectedIcon, stg.SourceIconPath, StringComparison.OrdinalIgnoreCase))
                    return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"Id={src.Id} SourceIconPath 不匹配");
            }

            // 3. tags 与 clip_item_tags 集合比较（§7.2-7.3）
            var sourceTags = ReadAllTags(sourceDbPath);
            var stagingTags = ReadAllTags(stagingDbPath);
            if (!sourceTags.SequenceEqual(stagingTags))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, "tags 集合不一致");

            var sourceCt = ReadAllClipItemTags(sourceDbPath);
            var stagingCt = ReadAllClipItemTags(stagingDbPath);
            if (!sourceCt.SequenceEqual(stagingCt))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, "clip_item_tags 集合不一致");
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"DB 验证失败: {ex.Message}");
        }

        // 3. DB/manifest 之外的文件逐文件 hash 比较（排除 marker + DB + manifests）
        try
        {
        var stagingFiles = EnumerateSourceFiles(staging);
        var excludedSet = new HashSet<string>(ExcludedFiles, StringComparer.OrdinalIgnoreCase);

        foreach (string sourceFile in sourceFiles)
        {
            string fileName = Path.GetFileName(sourceFile);
            if (excludedSet.Contains(fileName)) continue;
            // BLOCKER-1: DB 与 manifest 走结构化验证 (§7.1-7.5)，不参与整文件 byte-hash
            if (fileName.Equals(DbFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.EndsWith(".clipora-files.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsRootMigrationMarker(sourceRoot, sourceFile)) continue;

            string relative = sourceFile[(sourceRoot.Length + 1)..];
            if (relative.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                relative = relative[1..];
            string stagingFile = Path.Combine(staging, relative);

            if (!File.Exists(stagingFile))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"staging 缺少文件: {relative}");

            long srcLen = new FileInfo(sourceFile).Length;
            long dstLen = new FileInfo(stagingFile).Length;
            if (srcLen != dstLen)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"长度不一致: {relative}");

            using var srcStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var dstStream = new FileStream(stagingFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (!SHA256.HashData(srcStream).SequenceEqual(SHA256.HashData(dstStream)))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"SHA-256 不一致: {relative}");
        }

        // 确保 staging 没有多余文件（除 marker 外）
        foreach (string stagingFile in stagingFiles)
        {
            string fileName = Path.GetFileName(stagingFile);
            if (IsRootMigrationMarker(staging, stagingFile)) continue;

            string relative = stagingFile[(staging.Length + 1)..];
            if (relative.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                relative = relative[1..];

            string sourceFile = Path.Combine(sourceRoot, relative);
            if (!File.Exists(sourceFile))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"staging 多余文件: {relative}");
        }

        } // end try for byte-hash comparison
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"逐文件验证 IO 异常: {ex.Message}");
        }

        // 4a. 所有 manifest 内容验证（OriginalPath 原样，StoredPath 指向 target 路径，staging 文件存在）
        var manifestFiles = sourceFiles
            .Where(f => f.EndsWith(".clipora-files.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string sourceManifest in manifestFiles)
        {
            string relative = sourceManifest[(sourceRoot.Length + 1)..];
            if (relative.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                relative = relative[1..];
            string stagingManifest = Path.Combine(staging, relative);

            var sourceM = ClipFileManifest.Load(sourceManifest);
            if (sourceM is null || sourceM.Entries.Count == 0)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"source manifest 不可解析: {relative}");

            var stagingM = ClipFileManifest.Load(stagingManifest);
            if (stagingM is null || stagingM.Entries.Count != sourceM.Entries.Count)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"staging manifest 不可解析/条目数不一致: {relative}");

            for (int i = 0; i < sourceM.Entries.Count; i++)
            {
                if (!string.Equals(sourceM.Entries[i].OriginalPath, stagingM.Entries[i].OriginalPath, StringComparison.OrdinalIgnoreCase))
                    return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"manifest {relative} entry[{i}] OriginalPath 被篡改");

                if (!sourceM.IsReferenceOnly && stagingM.IsReferenceOnly != false)
                    return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"manifest {relative} IsReferenceOnly 不一致");

                if (!sourceM.IsReferenceOnly)
                {
                    string expectedStored = StoragePathRewriter.RewriteManagedPath(
                        sourceM.Entries[i].StoredPath, sourceRoot, targetRoot, "manifest StoredPath")
                        ?? string.Empty;
                    if (!string.Equals(expectedStored, stagingM.Entries[i].StoredPath, StringComparison.OrdinalIgnoreCase))
                        return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false,
                            $"manifest {relative} entry[{i}] StoredPath 不匹配（期望={expectedStored}）");

                    // staging 对应文件存在（StoredPath 已改写为 final target 路径，需转为 staging 路径）
                    if (!string.IsNullOrEmpty(stagingM.Entries[i].StoredPath))
                    {
                        string relFromTarget;
                        try { relFromTarget = Path.GetRelativePath(targetRoot, stagingM.Entries[i].StoredPath!); }
                        catch { relFromTarget = stagingM.Entries[i].StoredPath!; }
                        string stagingFilePath = Path.Combine(staging, relFromTarget);
                        if (!File.Exists(stagingFilePath))
                            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false,
                                $"manifest {relative} entry[{i}] StoredPath 文件缺失: {stagingM.Entries[i].StoredPath}");
                    }
                }
            }
        }

        // 4b. settings.json 若存在必须可解析
        string settingsPath = Path.Combine(staging, SettingsFileName);
        if (File.Exists(settingsPath))
        {
            try
            {
                string json = File.ReadAllText(settingsPath);
                System.Text.Json.JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, $"settings.json 不可解析: {ex.Message}");
            }
        }

        // 5. marker 再次匹配
        var readMarker = StorageMigrationMarker.Read(staging);
        if (readMarker is null)
            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, "marker 丢失");
        if (!Guid.TryParse(readMarker.MigrationId, out Guid rmId) || rmId != migrationId)
            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, sourceRoot, false, "marker MigrationId 不匹配");

        // marker 完成
        StorageMigrationMarker.UpdatePhase(staging, StorageMigrationPhase.Verifying, markerData);

        return Ok(StorageMigrationPhase.Verifying, false, sourceRoot, null);
    }

    private StorageMigrationResult VerifyTarget(
        string targetDir, string sourceRoot, string targetRoot,
        Guid migrationId, IProgress<StorageMigrationProgress>? progress)
    {
        string targetDbPath = Path.Combine(targetDir, DbFileName);
        if (!File.Exists(targetDbPath))
            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, targetDir, false, "target clipora.db 不存在");

        // 基本验证：integrity + 表非空
        string cs = new SqliteConnectionStringBuilder { DataSource = targetDbPath, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var icCmd = connection.CreateCommand();
            icCmd.CommandText = "PRAGMA integrity_check;";
            var icResult = icCmd.ExecuteScalar() as string;
            if (!string.Equals(icResult?.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, targetDir, false, $"target integrity_check: {icResult}");

            long ct = CountTable(targetDbPath, "clip_items");
            if (ct == 0)
                return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, targetDir, false, "target clip_items 为空");
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Verifying, StorageMigrationError.VerificationFailed, targetDir, false, $"target DB 验证失败: {ex.Message}");
        }

        return Ok(StorageMigrationPhase.Verifying, true, targetDir, null);
    }

    private StorageMigrationResult SwitchAndCommit(
        string sourceRoot, string targetRoot, Guid migrationId,
        IProgress<StorageMigrationProgress>? progress, bool promoted)
    {
        _faultInjector.ThrowIfRequested(StorageMigrationFaultPoint.DuringSwitch);

        try
        {
            _stateStore.CommitTarget(targetRoot, sourceRoot, migrationId);
        }
        catch (Exception ex)
        {
            return Fail(StorageMigrationPhase.Switching, StorageMigrationError.SwitchFailed,
                promoted ? targetRoot : sourceRoot, promoted, $"CommitTarget 失败: {ex.Message}");
        }

        // 更新 marker 为 Completed
        string markerPath = Path.Combine(targetRoot, ".clipora-migration.json");
        if (File.Exists(markerPath))
        {
            try
            {
                var data = StorageMigrationMarker.Read(targetRoot);
                if (data is not null)
                    StorageMigrationMarker.UpdatePhase(targetRoot, StorageMigrationPhase.Completed, data);
            }
            catch
            {
                // marker 更新失败不影响成功结果（state 已指向 target）
            }
        }

        Report(progress, StorageMigrationPhase.Completed, 0, 0, 0, 0);
        return Ok(StorageMigrationPhase.Completed, promoted, targetRoot, "迁移完成");
    }

    // ─── Helpers ───

    private static string GetStagingPath(string targetRoot, Guid migrationId)
    {
        string? parent = Path.GetDirectoryName(targetRoot) ?? string.Empty;
        string targetName = Path.GetFileName(targetRoot);
        string stagingName = $".{targetName}.clipora-migrating-{migrationId:N}";
        return Path.Combine(parent, stagingName);
    }

    private static List<string> EnumerateSourceFiles(string root)
    {
        var files = new List<string>();
        EnumerateRecursive(root, root, files);
        return files;
    }

    private static void EnumerateRecursive(string root, string current, List<string> accumulator)
    {
        foreach (string file in Directory.GetFiles(current))
            accumulator.Add(file);
        foreach (string dir in Directory.GetDirectories(current))
            EnumerateRecursive(root, dir, accumulator);
    }

    private static long CountTable(string dbPath, string tableName)
    {
        string cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>全列读取 clip_items（用于逐行结构化比较）。</summary>
    private static List<ClipItemRow> ReadAllClipItems(string dbPath)
    {
        var result = new List<ClipItemRow>();
        string cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Type, PreviewText, TextContent, RefPath, ThumbnailPath, SourceApp, SourceIconPath, CreatedAt, IsPinned, ContentHash, SizeBytes, IsDeleted, DeletedAt FROM clip_items ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ClipItemRow(
                Id: reader.GetInt64(0),
                Type: reader.GetInt32(1),
                PreviewText: reader.GetString(2),
                TextContent: reader.IsDBNull(3) ? null : reader.GetString(3),
                RefPath: reader.IsDBNull(4) ? null : reader.GetString(4),
                ThumbnailPath: reader.IsDBNull(5) ? null : reader.GetString(5),
                SourceApp: reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceIconPath: reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt: reader.GetString(8),
                IsPinned: reader.GetInt32(9),
                ContentHash: reader.GetString(10),
                SizeBytes: reader.GetInt64(11),
                IsDeleted: reader.GetInt32(12),
                DeletedAt: reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return result;
    }

    /// <summary>读取 tags 全行（主键 + 值集合比较）。</summary>
    private static List<(long Id, string Name, string Color, int SortOrder)> ReadAllTags(string dbPath)
    {
        var result = new List<(long, string, string, int)>();
        string cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Color, SortOrder FROM tags ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
    }

    /// <summary>读取 clip_item_tags 全行（集合比较）。</summary>
    private static List<(long ClipItemId, long TagId)> ReadAllClipItemTags(string dbPath)
    {
        var result = new List<(long, long)>();
        string cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ClipItemId, TagId FROM clip_item_tags ORDER BY ClipItemId, TagId;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return result;
    }

    private sealed record ClipItemRow(
        long Id, int Type, string PreviewText, string? TextContent,
        string? RefPath, string? ThumbnailPath, string? SourceApp, string? SourceIconPath,
        string CreatedAt, int IsPinned, string ContentHash, long SizeBytes,
        int IsDeleted, string? DeletedAt);

    private static long ComputeSourceSize(string root)
    {
        try
        {
            var files = EnumerateSourceFiles(root);
            return files
                .Where(f => !ShouldExcludeSourceFile(root, f))
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    private static bool ShouldExcludeSourceFile(string sourceRoot, string file) =>
        ExcludedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
        || IsRootMigrationMarker(sourceRoot, file);

    private static bool IsRootMigrationMarker(string root, string file)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(file),
                Path.GetFullPath(StorageMigrationMarker.GetMarkerPath(root)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNetworkPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            // 允许本地扩展路径 \\?\C:\...，拒绝 \\host\share 和 \\?\UNC\...
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                bool isExtendedLocal = full.StartsWith(@"\\?\", StringComparison.Ordinal)
                    && !full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
                if (!isExtendedLocal)
                    return true;
            }
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                try
                {
                    if (new DriveInfo(root).DriveType == DriveType.Network)
                        return true;
                }
                catch { }
            }
            return false;
        }
        catch { return true; } // fail-closed
    }

    private static bool AreRelated(string a, string b)
    {
        try
        {
            string fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase))
                return true;

            string sa = fa + Path.DirectorySeparatorChar;
            string sb = fb + Path.DirectorySeparatorChar;

            return fa.StartsWith(sb, StringComparison.OrdinalIgnoreCase)
                || fb.StartsWith(sa, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; } // fail-closed
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return false;
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; } // fail-closed
    }

    private static string? FindReparsePoints(string root)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        return dir;
                }
                catch { return dir; } // fail-closed
            }
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        return file;
                }
                catch { return file; } // fail-closed
            }
            return null;
        }
        catch { return root; } // fail-closed
    }

    private static void Report(
        IProgress<StorageMigrationProgress>? progress,
        StorageMigrationPhase phase,
        int completedFiles, int totalFiles,
        long completedBytes, long totalBytes)
    {
        if (progress is null) return;
        try
        {
            progress.Report(new StorageMigrationProgress(phase, completedFiles, totalFiles, completedBytes, totalBytes));
        }
        catch
        {
            // 回调异常隔离：吞并并继续，不得破坏迁移安全
        }
    }

    private static StorageMigrationResult Ok(StorageMigrationPhase phase, bool promoted, string activeRoot, string? detail)
        => new(true, phase, StorageMigrationError.None, activeRoot, promoted, detail);

    private static StorageMigrationResult Fail(StorageMigrationPhase phase, StorageMigrationError error, string activeRoot, bool promoted, string? detail)
        => new(false, phase, error, activeRoot, promoted, detail);

    private StorageMigrationResult CleanupFail(
        StorageMigrationPhase phase, StorageMigrationError error,
        string activeRoot, bool promoted, string staging, Guid migrationId, string? detail)
    {
        // promote 成功后（Switching 及之后）失败：保留 target + pending 供恢复重试 commit，绝不删 target。
        // 此时 staging 已被 Move 走、不存在，下面的删除块不会命中。
        bool afterPromote = phase >= StorageMigrationPhase.Switching;

        // 清理 app-owned staging，并跟踪清理是否真正成功（§10：只有清理成功后才清 pending）。
        // cleanupOk 起始为 true（无 staging 可清也算成功）；删除失败或目录非 app-owned 则置 false。
        bool cleanupOk = true;
        if (Directory.Exists(staging))
        {
            try
            {
                string targetRoot = Path.GetDirectoryName(staging) is not null
                    ? Path.Combine(Path.GetDirectoryName(staging)!,
                        Path.GetFileName(staging)
                            .Replace(".clipora-migrating-" + migrationId.ToString("N"), "")
                            .TrimStart('.'))
                    : string.Empty;

                if (StorageMigrationMarker.IsAppOwned(staging, migrationId, activeRoot, targetRoot, out _))
                {
                    Directory.Delete(staging, recursive: true);
                }
                else
                {
                    // 未知 / marker 不匹配目录绝不删除，且视为清理未完成 → 保留 pending 供人工处理。
                    cleanupOk = false;
                }
            }
            catch { cleanupOk = false; /* 清理失败不扩大范围，保留 pending 供人工处理 */ }
        }

        // promote 成功前的失败：仅在清理成功后清 pending（§10）；清理失败则保留 pending。
        // promote 成功后的失败：保留 pending（恢复路径据此重试 commit）。
        if (!afterPromote && cleanupOk)
        {
            try { _stateStore.ClearPending(migrationId); } catch { /* ignore */ }
        }

        return Fail(phase, error, activeRoot, promoted, detail);
    }

}
