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
        string journalPath = Path.Combine(stagingDir, "import-journal.json");
        if (!File.Exists(journalPath))
        {
            // 无 journal → 孤立 staging 目录，可能是成功清理的残留，安全删除
            try { Directory.Delete(stagingDir, true); } catch { }
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
            // 损坏的 journal → 无法确定状态，保留 staging 不做清理
            return;
        }

        if (journal is null || !Guid.TryParse(journal.ImportId, out Guid importId))
            return;

        bool committed = _db.HasImportSentinel(importId);

        if (committed)
        {
            // 事务已提交：保留最终文件，清理 journal 和 staging
            try { File.Delete(journalPath); } catch { }
            try { Directory.Delete(stagingDir, true); } catch { }

            // 再在独立事务中删哨兵
            _db.DeleteImportSentinel(importId);
        }
        else
        {
            // 事务未提交：删除本批最终文件和 staging
            if (journal.FinalPaths is not null)
            {
                foreach (string path in journal.FinalPaths)
                {
                    try { File.Delete(path); } catch { }
                }
            }

            // 先删 journal，再删 staging
            try { File.Delete(journalPath); } catch { }
            try { Directory.Delete(stagingDir, true); } catch { }
        }
    }
}
