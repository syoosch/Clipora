using Microsoft.Data.Sqlite;

namespace Clipora.Services;

/// <summary>打开 SQLite 连接并确保表结构存在。</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(AppPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DbPath,
        }.ToString();

        Initialize();
    }

    /// <summary>打开一个新连接（调用方负责释放）。每个连接启用外键级联。</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS clip_items (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Type          INTEGER NOT NULL,
                PreviewText   TEXT    NOT NULL DEFAULT '',
                TextContent   TEXT,
                RefPath       TEXT,
                ThumbnailPath TEXT,
                SourceApp     TEXT,
                SourceIconPath TEXT,
                CreatedAt     TEXT    NOT NULL,
                IsPinned      INTEGER NOT NULL DEFAULT 0,
                ContentHash   TEXT    NOT NULL DEFAULT '',
                SizeBytes     INTEGER NOT NULL DEFAULT 0,
                IsDeleted     INTEGER NOT NULL DEFAULT 0,
                DeletedAt     TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_clip_order
                ON clip_items(IsDeleted, IsPinned DESC, CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS ix_clip_hash
                ON clip_items(ContentHash);

            CREATE TABLE IF NOT EXISTS tags (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL,
                Color     TEXT    NOT NULL DEFAULT '#0078D4',
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS clip_item_tags (
                ClipItemId INTEGER NOT NULL,
                TagId      INTEGER NOT NULL,
                PRIMARY KEY (ClipItemId, TagId),
                FOREIGN KEY (ClipItemId) REFERENCES clip_items(Id) ON DELETE CASCADE,
                FOREIGN KEY (TagId)      REFERENCES tags(Id)       ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS backup_import_batches (
                ImportId       TEXT NOT NULL PRIMARY KEY,
                CommittedAtUtc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // —— M5.1 附加式 schema 演进：补齐缺失列（幂等，只增不改不删） ——
        MigrateOcrColumns(connection);
    }

    /// <summary>M5.2：WAL checkpoint + VACUUM INTO 生成一致性快照到指定路径。</summary>
    public void CheckpointAndVacuumInto(string tempDbPath)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();

        // WAL checkpoint TRUNCATE: 将所有 WAL 帧写入主 DB 文件，然后截断 WAL
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();

        // VACUUM INTO: 创建独立的一致副本（不修改当前连接状态）
        string escaped = tempDbPath.Replace("'", "''");
        cmd.CommandText = $"VACUUM INTO '{escaped}';";
        cmd.ExecuteNonQuery();
    }

    /// <summary>M5.2：读取临时 DB（禁用池化，避免 VACUUM INTO 后的文件锁定）。</summary>
    public SqliteConnection OpenTemp(string dbPath)
    {
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

    /// <summary>M5.2：检查指定 ImportId 的哨兵是否存在（事务已提交）。</summary>
    public bool HasImportSentinel(Guid importId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM backup_import_batches WHERE ImportId = $id;";
        cmd.Parameters.AddWithValue("$id", importId.ToString("D"));
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>M5.2：删除指定 ImportId 的哨兵（必须在 journal 删除之后、独立事务中调用）。</summary>
    public void DeleteImportSentinel(Guid importId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM backup_import_batches WHERE ImportId = $id;";
        cmd.Parameters.AddWithValue("$id", importId.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>M5.1：补齐 OcrText / OcrStatus 列（幂等）。</summary>
    private static void MigrateOcrColumns(SqliteConnection connection)
    {
        // 读取当前列名集合
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(clip_items);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1)); // column name 在 ordinal 1
        }

        // 按需补列（附加式，永不修改/删除既有列）
        if (!existing.Contains("OcrText"))
        {
            using var add = connection.CreateCommand();
            add.CommandText = "ALTER TABLE clip_items ADD COLUMN OcrText TEXT NULL;";
            add.ExecuteNonQuery();
        }

        if (!existing.Contains("OcrStatus"))
        {
            using var add = connection.CreateCommand();
            add.CommandText = "ALTER TABLE clip_items ADD COLUMN OcrStatus INTEGER NOT NULL DEFAULT 0;";
            add.ExecuteNonQuery();
        }
    }
}
