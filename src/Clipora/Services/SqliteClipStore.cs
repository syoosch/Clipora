using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary><see cref="IClipStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteClipStore : IClipStore
{
    private readonly Database _db;

    public SqliteClipStore(Database db) => _db = db;

    public long Add(ClipItem item, bool mergeDuplicates)
    {
        using var c = _db.Open();

        if (mergeDuplicates && !string.IsNullOrEmpty(item.ContentHash))
        {
            using var find = c.CreateCommand();
            find.CommandText = "SELECT Id FROM clip_items WHERE ContentHash = $h AND IsDeleted = 0 LIMIT 1;";
            find.Parameters.AddWithValue("$h", item.ContentHash);
            if (find.ExecuteScalar() is { } existing)
            {
                long existingId = Convert.ToInt64(existing);
                using var bump = c.CreateCommand();
                bump.CommandText = "UPDATE clip_items SET CreatedAt = $t WHERE Id = $id;";
                bump.Parameters.AddWithValue("$t", Iso(item.CreatedAt));
                bump.Parameters.AddWithValue("$id", existingId);
                bump.ExecuteNonQuery();
                return existingId;
            }
        }

        using var insert = c.CreateCommand();
        insert.CommandText = @"
INSERT INTO clip_items
    (Type, PreviewText, TextContent, RefPath, ThumbnailPath, SourceApp, SourceIconPath,
     CreatedAt, IsPinned, ContentHash, SizeBytes, IsDeleted, DeletedAt,
     OcrText, OcrStatus)
VALUES
    ($type, $preview, $text, $ref, $thumb, $src, $icon,
     $created, $pinned, $hash, $size, 0, NULL,
     $ocrText, $ocrStatus);";
        insert.Parameters.AddWithValue("$type", (int)item.Type);
        insert.Parameters.AddWithValue("$preview", item.PreviewText ?? string.Empty);
        insert.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ref", (object?)item.RefPath ?? DBNull.Value);
        insert.Parameters.AddWithValue("$thumb", (object?)item.ThumbnailPath ?? DBNull.Value);
        insert.Parameters.AddWithValue("$src", (object?)item.SourceApp ?? DBNull.Value);
        insert.Parameters.AddWithValue("$icon", (object?)item.SourceIconPath ?? DBNull.Value);
        insert.Parameters.AddWithValue("$created", Iso(item.CreatedAt));
        insert.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
        insert.Parameters.AddWithValue("$hash", item.ContentHash ?? string.Empty);
        insert.Parameters.AddWithValue("$size", item.SizeBytes);
        insert.Parameters.AddWithValue("$ocrText", (object?)item.OcrText ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ocrStatus", (int)item.OcrStatus);
        insert.ExecuteNonQuery();

        using var idCmd = c.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(idCmd.ExecuteScalar());
    }

    public IReadOnlyList<ClipItem> Query(ClipQuery q)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();

        var sql = new StringBuilder("SELECT * FROM clip_items WHERE IsDeleted = $deleted");
        cmd.Parameters.AddWithValue("$deleted", q.IncludeDeleted ? 1 : 0);

        if (q.Type is { } type)
        {
            sql.Append(" AND Type = $type");
            cmd.Parameters.AddWithValue("$type", (int)type);
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            sql.Append(" AND (PreviewText LIKE $s OR IFNULL(TextContent, '') LIKE $s OR IFNULL(OcrText, '') LIKE $s OR REPLACE(IFNULL(OcrText,''), ' ', '') LIKE REPLACE($s, ' ', ''))");
            cmd.Parameters.AddWithValue("$s", "%" + q.Search.Trim() + "%");
        }
        if (q.Since is { } since)
        {
            sql.Append(" AND CreatedAt >= $since");
            cmd.Parameters.AddWithValue("$since", Iso(since));
        }
        if (q.Until is { } until)
        {
            sql.Append(" AND CreatedAt <= $until");
            cmd.Parameters.AddWithValue("$until", Iso(until));
        }
        if (q.TagId is { } tagId)
        {
            sql.Append(" AND Id IN (SELECT ClipItemId FROM clip_item_tags WHERE TagId = $tag)");
            cmd.Parameters.AddWithValue("$tag", tagId);
        }

        sql.Append(q.PrioritizePinned
            ? " ORDER BY IsPinned DESC, CreatedAt DESC, Id DESC LIMIT $take OFFSET $skip;"
            : " ORDER BY CreatedAt DESC, Id DESC LIMIT $take OFFSET $skip;");
        cmd.Parameters.AddWithValue("$take", q.Take);
        cmd.Parameters.AddWithValue("$skip", q.Skip);
        cmd.CommandText = sql.ToString();

        var list = new List<ClipItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(MapFrom(reader));
        return list;
    }


    public ClipItem? GetById(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM clip_items WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapFrom(reader) : null;
    }

    public void SetPinned(long id, bool pinned) =>
        Exec("UPDATE clip_items SET IsPinned = $p WHERE Id = $id;", ("$p", pinned ? 1 : 0), ("$id", id));

    public void SoftDelete(long id) =>
        Exec("UPDATE clip_items SET IsDeleted = 1, DeletedAt = $t WHERE Id = $id;", ("$t", Iso(DateTime.UtcNow)), ("$id", id));

    public void Restore(long id) =>
        Exec("UPDATE clip_items SET IsDeleted = 0, DeletedAt = NULL WHERE Id = $id;", ("$id", id));

    public void HardDelete(long id) =>
        Exec("DELETE FROM clip_items WHERE Id = $id;", ("$id", id));

    public IReadOnlyList<ClipItem> PurgeExpired(int retentionDays)
    {
        if (retentionDays <= 0)
            return Array.Empty<ClipItem>();

        using var c = _db.Open();
        using var txn = c.BeginTransaction();

        string cutoff = Iso(DateTime.UtcNow.AddDays(-retentionDays));

        using var select = c.CreateCommand();
        select.CommandText = "SELECT * FROM clip_items WHERE IsPinned = 0 AND IsDeleted = 0 AND CreatedAt < $cut;";
        select.Parameters.AddWithValue("$cut", cutoff);

        var purged = new List<ClipItem>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
                purged.Add(MapFrom(reader));
        }

        if (purged.Count > 0)
        {
            using var delete = c.CreateCommand();
            delete.CommandText = "DELETE FROM clip_items WHERE IsPinned = 0 AND IsDeleted = 0 AND CreatedAt < $cut;";
            delete.Parameters.AddWithValue("$cut", cutoff);
            delete.ExecuteNonQuery();
        }

        txn.Commit();
        return purged;
    }

    public IReadOnlyList<ClipItem> PurgeRecycleBin(int keepDays)
    {
        using var c = _db.Open();
        using var txn = c.BeginTransaction();

        string cutoff = Iso(DateTime.UtcNow.AddDays(-keepDays));

        using var select = c.CreateCommand();
        select.CommandText = "SELECT * FROM clip_items WHERE IsDeleted = 1 AND IFNULL(DeletedAt, CreatedAt) < $cut;";
        select.Parameters.AddWithValue("$cut", cutoff);

        var purged = new List<ClipItem>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
                purged.Add(MapFrom(reader));
        }

        if (purged.Count > 0)
        {
            using var delete = c.CreateCommand();
            delete.CommandText = "DELETE FROM clip_items WHERE IsDeleted = 1 AND IFNULL(DeletedAt, CreatedAt) < $cut;";
            delete.Parameters.AddWithValue("$cut", cutoff);
            delete.ExecuteNonQuery();
        }

        txn.Commit();
        return purged;
    }

    public void Clear(bool keepPinned)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = keepPinned
            ? "DELETE FROM clip_items WHERE IsPinned = 0;"
            : "DELETE FROM clip_items;";
        cmd.ExecuteNonQuery();
    }

    // —— M5.1 OCR ——

    public void SetOcrResult(long id, OcrOutcome outcome, string? text)
    {
        OcrStatus status = outcome switch
        {
            OcrOutcome.Recognized => OcrStatus.Completed,
            OcrOutcome.Empty => OcrStatus.Empty,
            OcrOutcome.Unsupported => OcrStatus.Unsupported,
            OcrOutcome.Failed => OcrStatus.Failed,
            _ => OcrStatus.Failed,
        };

        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE clip_items SET OcrText = $text, OcrStatus = $status WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (int)status);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ClipItem> ListPendingOcr(int take)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM clip_items WHERE IsDeleted = 0 AND Type = $type AND OcrStatus = $status ORDER BY CreatedAt DESC LIMIT $take;";
        cmd.Parameters.AddWithValue("$type", (int)ClipType.Image);
        cmd.Parameters.AddWithValue("$status", (int)OcrStatus.Pending);
        cmd.Parameters.AddWithValue("$take", take);

        var list = new List<ClipItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(MapFrom(reader));
        return list;
    }

    public void MarkLegacyImagesPending()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE clip_items SET OcrStatus = $pending WHERE Type = $type AND OcrStatus = $none AND IsDeleted = 0;";
        cmd.Parameters.AddWithValue("$pending", (int)OcrStatus.Pending);
        cmd.Parameters.AddWithValue("$type", (int)ClipType.Image);
        cmd.Parameters.AddWithValue("$none", (int)OcrStatus.None);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql, params (string name, object value)[] parameters)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime dt) =>
        dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static ClipItem MapFrom(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Type = (ClipType)r.GetInt32(r.GetOrdinal("Type")),
        PreviewText = r.GetString(r.GetOrdinal("PreviewText")),
        TextContent = NullableString(r, "TextContent"),
        RefPath = NullableString(r, "RefPath"),
        ThumbnailPath = NullableString(r, "ThumbnailPath"),
        SourceApp = NullableString(r, "SourceApp"),
        SourceIconPath = NullableString(r, "SourceIconPath"),
        CreatedAt = ParseUtc(r.GetString(r.GetOrdinal("CreatedAt"))),
        IsPinned = r.GetInt32(r.GetOrdinal("IsPinned")) != 0,
        ContentHash = r.GetString(r.GetOrdinal("ContentHash")),
        SizeBytes = r.GetInt64(r.GetOrdinal("SizeBytes")),
        IsDeleted = r.GetInt32(r.GetOrdinal("IsDeleted")) != 0,
        DeletedAt = NullableString(r, "DeletedAt") is { } d ? ParseUtc(d) : null,
        OcrText = NullableString(r, "OcrText"),
        OcrStatus = (OcrStatus)r.GetInt32(r.GetOrdinal("OcrStatus")),
    };

    private static string? NullableString(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }
}
