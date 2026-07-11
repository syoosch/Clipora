using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Clipora.Services;

/// <summary>数据库损坏自动恢复结果。</summary>
public sealed class DatabaseRecoveryResult
{
    /// <summary>是否检测到损坏并完成备份（true 时三个路径均非空）。</summary>
    public bool Recovered { get; init; }

    /// <summary>损坏数据库的原位置（恢复后该位置将由 Database 重建为空库）。</summary>
    public string? CorruptPath { get; init; }

    /// <summary>损坏文件被移动到的备份位置。</summary>
    public string? BackupPath { get; init; }

    /// <summary>新建空数据库的位置（与 CorruptPath 同一路径）。</summary>
    public string? NewPath { get; init; }

    /// <summary>诊断说明。</summary>
    public string? Detail { get; init; }

    internal static DatabaseRecoveryResult Healthy(string? detail = null) =>
        new() { Recovered = false, Detail = detail };
}

/// <summary>
/// 启动时在构造 <see cref="Database"/> / 执行任何查询前检测主库是否损坏。
/// 损坏（malformed / not a database）时把坏库及其 WAL/SHM 移到 Root\corrupt-backup\&lt;时间戳&gt;\，
/// 腾出原位置让 Database 重建空库，并回传损坏/备份/新库三处位置供 UI 明确提示。
/// 仅对**明确的损坏**动手；忙/锁/权限等非损坏错误一律按健康处理，绝不删除用户数据。
/// </summary>
public sealed class DatabaseRecoveryService
{
    private readonly AppPaths _paths;

    public DatabaseRecoveryService(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>检测主库；损坏则备份并腾出位置。健康或不存在则原样返回（不恢复）。</summary>
    public DatabaseRecoveryResult EnsureHealthy()
    {
        string dbPath = _paths.DbPath;
        try
        {
            if (!File.Exists(dbPath))
                return DatabaseRecoveryResult.Healthy("数据库不存在，将新建。");

            if (IsHealthy(dbPath))
                return DatabaseRecoveryResult.Healthy();

            // 明确损坏：释放可能的连接句柄，再把坏库及 WAL/SHM 移走备份。
            try { SqliteConnection.ClearAllPools(); } catch { }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string backupDir = Path.Combine(_paths.Root, "corrupt-backup", stamp);
            Directory.CreateDirectory(backupDir);

            string backupDb = Path.Combine(backupDir, "clipora.db");
            MoveIfExists(dbPath, backupDb);
            MoveIfExists(dbPath + "-wal", backupDb + "-wal");
            MoveIfExists(dbPath + "-shm", backupDb + "-shm");

            return new DatabaseRecoveryResult
            {
                Recovered = true,
                CorruptPath = dbPath,
                BackupPath = backupDb,
                NewPath = dbPath,
                Detail = "数据库文件损坏，已备份损坏文件并将在原位置重建空库。",
            };
        }
        catch (Exception ex)
        {
            // 恢复本身失败：不阻断启动（后续 Database 构造可能仍异常，但不比现状更差）。
            return DatabaseRecoveryResult.Healthy($"恢复检查失败：{ex.Message}");
        }
    }

    private static void MoveIfExists(string src, string dest)
    {
        if (File.Exists(src))
            File.Move(src, dest, overwrite: false);
    }

    internal static bool IsHealthyForTest(string dbPath) => IsHealthy(dbPath);

    /// <summary>
    /// 用 PRAGMA quick_check 判定主库是否健康。明确损坏（SQLITE_CORRUPT/NOTADB，
    /// 或 quick_check 返回非 "ok"）→ false；其余一切（忙/锁/权限/未知）→ true，避免误删健康库。
    /// </summary>
    private static bool IsHealthy(string dbPath)
    {
        SqliteConnection? conn = null;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadWrite,
            };
            conn = new SqliteConnection(builder.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                string result = reader.GetString(0);
                return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            }
            return true; // 无结果行：非典型，保守按健康处理
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */
                                         || ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
        {
            return false;
        }
        catch (SqliteException)
        {
            // 忙/锁/权限等非损坏错误：不可判定为损坏，按健康处理。
            return true;
        }
        catch
        {
            return true;
        }
        finally
        {
            conn?.Dispose();
            try { SqliteConnection.ClearAllPools(); } catch { }
        }
    }
}
