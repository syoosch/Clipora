using System.IO;
using System.Text;
using System.Text.Json;

namespace Clipora.Services;

/// <summary>
/// M5.2 备份导入崩溃恢复服务。
/// 启动时扫描 .backup-import-staging/ 下的 journal，根据 sentinel 决定保留或回滚。
/// </summary>
public sealed class BackupImportRecoveryService
{
    private readonly Database _db;
    private readonly string _root;

    public BackupImportRecoveryService(Database db, string root)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>扫描并恢复所有未完成的导入。幂等，重复调用无副作用。</summary>
    public void RecoverAll()
    {
        string stagingParent = Path.Combine(_root, ".backup-import-staging");
        if (!Directory.Exists(stagingParent))
            return;

        foreach (string importDir in Directory.GetDirectories(stagingParent))
        {
            try
            {
                RecoverOne(importDir);
            }
            catch
            {
                // 单个导入恢复失败不阻止其他导入恢复
            }
        }
    }

    private void RecoverOne(string stagingDir)
    {
        string stagingParent = Path.Combine(_root, ".backup-import-staging");
        string directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(stagingDir));
        if (!Guid.TryParseExact(directoryName, "D", out Guid directoryImportId))
        {
            BackupPathPolicy.SafeDeleteTree(stagingParent, stagingDir);
            return;
        }

        string v2JournalPath = Path.Combine(stagingDir, "state", "import-journal.json");
        string legacyJournalPath = Path.Combine(stagingDir, "import-journal.json");
        string journalPath = File.Exists(v2JournalPath) ? v2JournalPath : legacyJournalPath;
        if (!File.Exists(journalPath))
        {
            // 无 journal → 只清理 staging，绝不根据归档内容推断外部路径。
            BackupPathPolicy.SafeDeleteTree(stagingParent, stagingDir);
            return;
        }

        BackupImportJournal? journal;
        try
        {
            string json = File.ReadAllText(journalPath, Encoding.UTF8);
            journal = JsonSerializer.Deserialize(json, JsonContext.Default.BackupImportJournal);
        }
        catch
        {
            // 损坏 journal 只清 staging，不碰任何最终文件。
            BackupPathPolicy.SafeDeleteTree(stagingParent, stagingDir);
            return;
        }

        if (journal is null
            || !Guid.TryParseExact(journal.ImportId, "D", out Guid importId)
            || importId != directoryImportId
            || !TryResolveFinalPaths(journal, stagingDir, out List<string> finalPaths))
        {
            BackupPathPolicy.SafeDeleteTree(stagingParent, stagingDir);
            return;
        }

        bool committed = _db.HasImportSentinel(importId);

        if (committed)
        {
            // 事务已提交：保留最终文件，清理 journal 和 staging
            try { File.Delete(journalPath); } catch { }
            BackupPathPolicy.SafeDeleteTree(stagingParent, stagingDir);

            // 再在独立事务中删哨兵
            _db.DeleteImportSentinel(importId);
        }
        else
        {
            // 事务未提交：删除本批最终文件和 staging
            foreach (string path in finalPaths)
            {
                try { File.Delete(path); } catch { }
            }

            // 先删 journal，再删 staging
            try { File.Delete(journalPath); } catch { }
            BackupPathPolicy.SafeDeleteTree(stagingParent, stagingDir);
        }
    }

    private bool TryResolveFinalPaths(
        BackupImportJournal journal, string stagingDir, out List<string> finalPaths)
    {
        finalPaths = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (journal.Version == 2)
            {
                if (journal.FinalRelativePaths is null)
                    return false;
                foreach (string relativePath in journal.FinalRelativePaths)
                {
                    if (!BackupPathPolicy.TryNormalizeManagedRelativePath(
                            relativePath, out string normalized, out _)
                        || !unique.Add(normalized))
                        return false;
                    finalPaths.Add(BackupPathPolicy.CombineUnderRoot(_root, normalized));
                }
                return true;
            }

            // v1 兼容仅接受与当前 staging 完全匹配、且所有绝对最终路径严格落在允许受管目录内的旧 journal。
            if (journal.Version is not 0 and not 1
                || string.IsNullOrWhiteSpace(journal.StagingRoot)
                || !Path.GetFullPath(journal.StagingRoot).Equals(
                    Path.GetFullPath(stagingDir), StringComparison.OrdinalIgnoreCase)
                || journal.FinalPaths is null)
                return false;

            foreach (string oldAbsolutePath in journal.FinalPaths)
            {
                if (!BackupPathPolicy.TryGetManagedRelativePath(_root, oldAbsolutePath, out string normalized)
                    || !unique.Add(normalized))
                    return false;
                finalPaths.Add(BackupPathPolicy.CombineUnderRoot(_root, normalized));
            }
            return true;
        }
        catch
        {
            finalPaths.Clear();
            return false;
        }
    }
}
