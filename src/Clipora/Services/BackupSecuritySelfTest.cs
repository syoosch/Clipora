using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipora.Models;
using Microsoft.Data.Sqlite;

namespace Clipora.Services;

/// <summary>真实文件/ZIP/SQLite 级备份对抗测试；仅由 --selftest 在 %TEMP% 下调用。</summary>
internal static class BackupSecuritySelfTest
{
    internal static void Run(string tempRoot, string validArchivePath)
    {
        string root = Path.Combine(tempRoot, "backup-security-root");
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root, new StorageLocationService(new MemoryStorageRootLocator(root)));
        var database = new Database(paths);
        var store = new SqliteClipStore(database);
        var backup = new BackupService(paths, database);

        RunPathPolicyTests();
        RunRecoveryBoundaryTests(tempRoot, paths, database);
        RunArchiveLayoutTests(tempRoot, validArchivePath, backup, paths, store);
        RunMaliciousDatabaseTests(tempRoot, validArchivePath, backup, paths, store);
        RunMotwPropagationTest(tempRoot, validArchivePath);
    }

    private static void RunPathPolicyTests()
    {
        string[] rejected =
        [
            "../outside.bin",
            "..\\outside.bin",
            "C:\\outside.bin",
            "C:outside.bin",
            "\\\\server\\share\\outside.bin",
            "images/file.txt:stream",
            "images/CON.txt",
            "images/name. ",
            "images//empty.png",
            "images/./dot.png",
            "other/file.bin",
        ];
        foreach (string path in rejected)
        {
            Assert(!BackupPathPolicy.TryNormalizeManagedRelativePath(path, out _, out _),
                $"BKP-SEC-PATH: 应拒绝 {path}");
        }

        Assert(BackupPathPolicy.TryNormalizeManagedRelativePath("images/good.png", out string normalized, out _)
            && normalized == "images/good.png", "BKP-SEC-PATH: 合法图片路径应通过");
        Assert(!BackupPathPolicy.TryNormalizeManagedRelativePath("IMAGES/G.PNG", out string upper, out _)
            || upper.Equals("IMAGES/G.PNG", StringComparison.Ordinal),
            "BKP-SEC-PATH: 路径策略不应做有损字符变换");
    }

    private static void RunRecoveryBoundaryTests(string tempRoot, AppPaths paths, Database database)
    {
        var recovery = new BackupImportRecoveryService(database, paths.Root);
        string outsideSentinel = Path.Combine(tempRoot, "backup-recovery-outside-sentinel.txt");
        File.WriteAllText(outsideSentinel, "must-survive");

        string[] maliciousRelativePaths =
        [
            "../backup-recovery-outside-sentinel.txt",
            "clipora.db",
            "settings.json",
            "images",
            "images/innocent.txt:evil",
        ];
        foreach (string maliciousPath in maliciousRelativePaths)
        {
            Guid id = Guid.NewGuid();
            string staging = Path.Combine(paths.Root, ".backup-import-staging", id.ToString("D"));
            string state = Path.Combine(staging, "state");
            Directory.CreateDirectory(state);
            var journal = new BackupImportJournal
            {
                Version = 2,
                ImportId = id.ToString("D"),
                FinalRelativePaths = [maliciousPath],
                Phase = "pre_commit",
            };
            File.WriteAllText(Path.Combine(state, "import-journal.json"),
                JsonSerializer.Serialize(journal, JsonContext.Default.BackupImportJournal));
            recovery.RecoverAll();
            Assert(File.Exists(outsideSentinel), $"BKP-SEC-REC: 恶意 journal 不得删除 sentinel（{maliciousPath}）");
            Assert(File.Exists(paths.DbPath), $"BKP-SEC-REC: 恶意 journal 不得删除数据库（{maliciousPath}）");
            Assert(!Directory.Exists(staging), $"BKP-SEC-REC: 非法 journal staging 应安全清理（{maliciousPath}）");
        }

        // v1 绝对越界路径必须拒绝；合法 v1 受管路径继续兼容回滚。
        Guid legacyBadId = Guid.NewGuid();
        string legacyBadStaging = Path.Combine(paths.Root, ".backup-import-staging", legacyBadId.ToString("D"));
        Directory.CreateDirectory(legacyBadStaging);
        var legacyBad = new BackupImportJournal
        {
            Version = 1,
            ImportId = legacyBadId.ToString("D"),
            StagingRoot = legacyBadStaging,
            FinalPaths = [outsideSentinel],
            Phase = "pre_commit",
        };
        File.WriteAllText(Path.Combine(legacyBadStaging, "import-journal.json"),
            JsonSerializer.Serialize(legacyBad, JsonContext.Default.BackupImportJournal));
        recovery.RecoverAll();
        Assert(File.Exists(outsideSentinel), "BKP-SEC-REC: v1 越界绝对路径不得删除");
        Assert(!Directory.Exists(legacyBadStaging), "BKP-SEC-REC: v1 越界 staging 应清理");

        Guid legacyGoodId = Guid.NewGuid();
        string legacyGoodStaging = Path.Combine(paths.Root, ".backup-import-staging", legacyGoodId.ToString("D"));
        Directory.CreateDirectory(legacyGoodStaging);
        string managedFile = Path.Combine(paths.ImagesDir, "legacy-safe-rollback.png");
        File.WriteAllText(managedFile, "rollback");
        var legacyGood = new BackupImportJournal
        {
            Version = 1,
            ImportId = legacyGoodId.ToString("D"),
            StagingRoot = legacyGoodStaging,
            FinalPaths = [managedFile],
            Phase = "pre_commit",
        };
        File.WriteAllText(Path.Combine(legacyGoodStaging, "import-journal.json"),
            JsonSerializer.Serialize(legacyGood, JsonContext.Default.BackupImportJournal));
        recovery.RecoverAll();
        Assert(!File.Exists(managedFile), "BKP-SEC-REC: 合法 v1 journal 应继续回滚受管文件");
        Assert(!Directory.Exists(legacyGoodStaging), "BKP-SEC-REC: 合法 v1 staging 应清理");

        // 损坏 journal 与 ImportId/目录不一致时只清 staging。
        Guid corruptId = Guid.NewGuid();
        string corruptStaging = Path.Combine(paths.Root, ".backup-import-staging", corruptId.ToString("D"));
        Directory.CreateDirectory(Path.Combine(corruptStaging, "state"));
        File.WriteAllText(Path.Combine(corruptStaging, "state", "import-journal.json"), "{broken");
        recovery.RecoverAll();
        Assert(File.Exists(outsideSentinel), "BKP-SEC-REC: 损坏 journal 不得碰外部文件");
        Assert(!Directory.Exists(corruptStaging), "BKP-SEC-REC: 损坏 journal staging 应清理");
    }

    private static void RunArchiveLayoutTests(
        string tempRoot,
        string validArchivePath,
        BackupService backup,
        AppPaths paths,
        SqliteClipStore store)
    {
        int baselineCount = store.Query(new ClipQuery { Take = 1000 }).Count;
        string outsideSentinel = Path.Combine(tempRoot, "backup-archive-outside-sentinel.txt");
        File.WriteAllText(outsideSentinel, "must-survive");

        AssertArchiveRejected(CreateArchiveWithExtraEntry(
            tempRoot, validArchivePath, "forged-root-journal.clpbak", "import-journal.json", "{}"),
            backup, paths, store, baselineCount, "伪造根 journal");
        Assert(File.Exists(outsideSentinel), "BKP-SEC-ZIP: 伪造根 journal 后外部 sentinel 必须存在");

        string[] unsafeEntries =
        [
            "../outside.bin",
            "payloads/../outside.bin",
            "C:/outside.bin",
            "C:outside.bin",
            "\\\\server\\share\\outside.bin",
            "payloads/images/file.txt:ads",
            "payloads/images/CON.txt",
        ];
        foreach (string unsafeEntry in unsafeEntries)
        {
            string name = "unsafe-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unsafeEntry)))[..8] + ".clpbak";
            AssertArchiveRejected(CreateArchiveWithExtraEntry(tempRoot, validArchivePath, name, unsafeEntry, "x"),
                backup, paths, store, baselineCount, unsafeEntry);
        }

        AssertArchiveRejected(CreateArchiveWithDuplicateEntry(
            tempRoot, validArchivePath, "duplicate-manifest.clpbak", "manifest.json"),
            backup, paths, store, baselineCount, "重复 manifest");
        AssertArchiveRejected(CreateArchiveWithDuplicateEntry(
            tempRoot, validArchivePath, "duplicate-db.clpbak", "clipora.db"),
            backup, paths, store, baselineCount, "重复 db");
        AssertArchiveRejected(CreateArchiveWithExtraEntry(
            tempRoot, validArchivePath, "undeclared-payload.clpbak", "payloads/images/undeclared.png", "x"),
            backup, paths, store, baselineCount, "未声明 payload");

        string caseDuplicate = FindFirstPayloadEntry(validArchivePath);
        AssertArchiveRejected(CreateArchiveWithDuplicateEntry(
            tempRoot, validArchivePath, "case-duplicate.clpbak", caseDuplicate.ToUpperInvariant()),
            backup, paths, store, baselineCount, "大小写重复 payload");

        AssertArchiveRejected(CreateArchiveWithModifiedManifest(
            tempRoot, validArchivePath, "bad-hash.clpbak", manifest =>
            {
                BackupManifestEntry entry = manifest.Entries.First();
                manifest.Entries[0] = entry with { Sha256 = new string('0', 64) };
            }), backup, paths, store, baselineCount, "损坏 SHA");

        AssertArchiveRejected(CreateArchiveWithModifiedManifest(
            tempRoot, validArchivePath, "bad-length.clpbak", manifest =>
            {
                BackupManifestEntry entry = manifest.Entries.First();
                manifest.Entries[0] = entry with { Length = entry.Length + 1 };
            }), backup, paths, store, baselineCount, "长度不符");

        AssertArchiveRejected(CreateArchiveWithModifiedManifest(
            tempRoot, validArchivePath, "bad-item-count.clpbak", manifest => manifest.ItemCount++),
            backup, paths, store, baselineCount, "ItemCount 不符");
    }

    private static void RunMaliciousDatabaseTests(
        string tempRoot,
        string validArchivePath,
        BackupService backup,
        AppPaths paths,
        SqliteClipStore store)
    {
        int baselineCount = store.Query(new ClipQuery { Take = 1000 }).Count;
        (string Name, string Sql)[] attacks =
        [
            ("trigger", "CREATE TRIGGER evil_trigger AFTER INSERT ON tags BEGIN SELECT 1; END;"),
            ("view", "CREATE VIEW evil_view AS SELECT * FROM clip_items;"),
            ("extra-table", "CREATE TABLE evil_table(Value TEXT);"),
            ("extra-column", "ALTER TABLE tags ADD COLUMN Evil TEXT;"),
            ("bad-clip-type", "UPDATE clip_items SET Type=999 WHERE Id=(SELECT MIN(Id) FROM clip_items);"),
            ("bad-ocr-status", "UPDATE clip_items SET OcrStatus=999 WHERE Id=(SELECT MIN(Id) FROM clip_items);"),
            ("bad-image-path", "UPDATE clip_items SET RefPath='C:\\outside\\evil.png' WHERE Type=4;"),
            ("bad-url", "UPDATE clip_items SET TextContent='file:///C:/secret.txt' WHERE Type=2;"),
            ("bad-fk", "PRAGMA foreign_keys=OFF; INSERT INTO clip_item_tags(ClipItemId,TagId) VALUES(999999,999999);"),
        ];

        foreach ((string name, string sql) in attacks)
        {
            string archive = CreateArchiveWithModifiedDatabase(tempRoot, validArchivePath, $"malicious-{name}.clpbak", sql);
            AssertArchiveRejected(archive, backup, paths, store, baselineCount, name);
        }
    }

    private static void RunMotwPropagationTest(string tempRoot, string validArchivePath)
    {
        string unavailableTarget = Path.Combine(tempRoot, "motw-unavailable", "payload.bin");
        BackupService.TryWriteInternetZoneForTest(unavailableTarget, 3);
        Assert(!Directory.Exists(Path.GetDirectoryName(unavailableTarget)),
            "BKP-SEC-MOTW: ADS 写入失败必须 best-effort 返回且不创建意外目录");

        string motwArchive = Path.Combine(tempRoot, "motw-valid.clpbak");
        File.Copy(validArchivePath, motwArchive, overwrite: true);
        bool adsAvailable;
        try
        {
            File.WriteAllText(motwArchive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            adsAvailable = true;
        }
        catch
        {
            adsAvailable = false;
        }

        if (!adsAvailable)
            return;

        string motwRoot = Path.Combine(tempRoot, "motw-restore-root");
        Directory.CreateDirectory(motwRoot);
        var paths = new AppPaths(motwRoot, new StorageLocationService(new MemoryStorageRootLocator(motwRoot)));
        var db = new Database(paths);
        var store = new SqliteClipStore(db);
        var service = new BackupService(paths, db);
        BackupImportResult result = service.ImportAsync(motwArchive, null, CancellationToken.None).Result;
        Assert(result.Ok, $"BKP-SEC-MOTW: 合法 ZoneId 归档应导入（{result.Error}）");
        ClipItem image = store.Query(new ClipQuery { Type = ClipType.Image, Take = 10 }).Single();
        string zone = File.ReadAllText(image.RefPath! + ":Zone.Identifier");
        Assert(zone.Contains("ZoneId=3", StringComparison.Ordinal), "BKP-SEC-MOTW: payload 应传播 ZoneId=3");
    }

    private static void AssertArchiveRejected(
        string archive,
        BackupService backup,
        AppPaths paths,
        SqliteClipStore store,
        int baselineCount,
        string scenario)
    {
        BackupImportResult result = backup.ImportAsync(archive, null, CancellationToken.None).Result;
        Assert(!result.Ok, $"BKP-SEC: {scenario} 必须被拒绝");
        Assert(store.Query(new ClipQuery { Take = 1000 }).Count == baselineCount,
            $"BKP-SEC: {scenario} 不得改变现有数据库");
        string stagingParent = Path.Combine(paths.Root, ".backup-import-staging");
        Assert(!Directory.Exists(stagingParent) || Directory.GetDirectories(stagingParent).Length == 0,
            $"BKP-SEC: {scenario} 失败后不得遗留无必要 staging");
    }

    private static string CreateArchiveWithExtraEntry(
        string tempRoot, string sourceArchive, string fileName, string entryName, string content)
    {
        string destination = Path.Combine(tempRoot, fileName);
        File.Copy(sourceArchive, destination, overwrite: true);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
        ZipArchiveEntry entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
        return destination;
    }

    private static string CreateArchiveWithDuplicateEntry(
        string tempRoot, string sourceArchive, string fileName, string entryName)
    {
        string destination = Path.Combine(tempRoot, fileName);
        File.Copy(sourceArchive, destination, overwrite: true);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
        using Stream stream = zip.CreateEntry(entryName).Open();
        stream.WriteByte(0);
        return destination;
    }

    private static string CreateArchiveWithModifiedManifest(
        string tempRoot, string sourceArchive, string fileName, Action<BackupManifest> mutate)
    {
        return CreateModifiedArchive(tempRoot, sourceArchive, fileName, extractedRoot =>
        {
            string manifestPath = Path.Combine(extractedRoot, "manifest.json");
            BackupManifest manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), JsonContext.Default.BackupManifest)!;
            mutate(manifest);
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, JsonContext.Default.BackupManifest), Encoding.UTF8);
        });
    }

    private static string CreateArchiveWithModifiedDatabase(
        string tempRoot, string sourceArchive, string fileName, string sql)
    {
        return CreateModifiedArchive(tempRoot, sourceArchive, fileName, extractedRoot =>
        {
            string dbPath = Path.Combine(extractedRoot, "clipora.db");
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            RefreshDatabaseManifest(extractedRoot);
        });
    }

    private static string CreateModifiedArchive(
        string tempRoot, string sourceArchive, string fileName, Action<string> mutate)
    {
        string extractionRoot = Path.Combine(tempRoot, "archive-mutate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);
        try
        {
            ZipFile.ExtractToDirectory(sourceArchive, extractionRoot);
            mutate(extractionRoot);
            string destination = Path.Combine(tempRoot, fileName);
            if (File.Exists(destination)) File.Delete(destination);
            using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
            foreach (string file in Directory.GetFiles(extractionRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(extractionRoot, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }
            return destination;
        }
        finally
        {
            try { Directory.Delete(extractionRoot, recursive: true); } catch { }
        }
    }

    private static void RefreshDatabaseManifest(string extractedRoot)
    {
        string manifestPath = Path.Combine(extractedRoot, "manifest.json");
        string dbPath = Path.Combine(extractedRoot, "clipora.db");
        BackupManifest manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath), JsonContext.Default.BackupManifest)!;
        int index = manifest.Entries.FindIndex(entry =>
            entry.RelativePath.Equals("clipora.db", StringComparison.OrdinalIgnoreCase));
        Assert(index >= 0, "BKP-SEC-HELPER: manifest 应包含 clipora.db");
        manifest.Entries[index] = new BackupManifestEntry(
            "clipora.db",
            new FileInfo(dbPath).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dbPath))));
        File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(manifest, JsonContext.Default.BackupManifest), Encoding.UTF8);
    }

    private static string FindFirstPayloadEntry(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return zip.Entries.First(entry => entry.FullName.StartsWith("payloads/", StringComparison.OrdinalIgnoreCase)).FullName;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
