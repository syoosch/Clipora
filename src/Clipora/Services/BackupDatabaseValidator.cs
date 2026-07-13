using System.Globalization;
using System.IO;
using System.Text.Json;
using Clipora.Models;
using Microsoft.Data.Sqlite;

namespace Clipora.Services;

/// <summary>把归档中的 SQLite 当作恶意数据库，在读取业务行前完成结构与语义验证。</summary>
internal static class BackupDatabaseValidator
{
    private const int SupportedSchemaVersion = 2;

    private static readonly IReadOnlyDictionary<string, string[]> RequiredTables =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["clip_items"] =
            [
                "Id", "Type", "PreviewText", "TextContent", "RefPath", "ThumbnailPath",
                "SourceApp", "SourceIconPath", "CreatedAt", "IsPinned", "ContentHash",
                "SizeBytes", "IsDeleted", "DeletedAt", "OcrText", "OcrStatus",
            ],
            ["tags"] = ["Id", "Name", "Color", "SortOrder"],
            ["clip_item_tags"] = ["ClipItemId", "TagId"],
            ["backup_import_batches"] = ["ImportId", "CommittedAtUtc"],
        };

    private static readonly HashSet<string> RequiredIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ix_clip_order",
        "ix_clip_hash",
    };

    internal static BackupDatabaseSnapshot ValidateAndRead(
        string dbPath,
        int schemaVersion,
        int manifestItemCount,
        string archiveRoot,
        IReadOnlySet<string> payloadRelativePaths)
    {
        if (schemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"不支持的数据库 schema 版本 {schemaVersion}");
        if (manifestItemCount < 0)
            throw new InvalidDataException("manifest ItemCount 不能为负数");

        using SqliteConnection connection = OpenUntrustedReadOnly(dbPath);
        ValidateIntegrity(connection);
        ValidateSchema(connection);

        var items = new List<ClipItem>();
        var itemIds = new HashSet<long>();
        var usedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM clip_items ORDER BY Id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ValidateClipRow(reader);
                ClipItem item = SqliteClipStore.MapFrom(reader);
                if (!itemIds.Add(item.Id))
                    throw new InvalidDataException("clip_items 存在重复 Id");
                ValidateItemPayloadSemantics(item, archiveRoot, payloadRelativePaths, usedPayloads);
                items.Add(item);
            }
        }

        if (items.Count != manifestItemCount)
            throw new InvalidDataException(
                $"manifest ItemCount={manifestItemCount} 与活动记录数 {items.Count} 不一致");

        var tags = new List<Tag>();
        var tagIds = new HashSet<long>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,Name,Color,SortOrder FROM tags ORDER BY Id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long id = ReadPositiveInteger(reader, 0, "tags.Id");
                string name = ReadString(reader, 1, "tags.Name");
                string color = ReadString(reader, 2, "tags.Color");
                long sortOrder = ReadInteger(reader, 3, "tags.SortOrder");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
                    throw new InvalidDataException("标签名称无效");
                if (!tagIds.Add(id))
                    throw new InvalidDataException("tags 存在重复 Id");
                if (sortOrder is < int.MinValue or > int.MaxValue)
                    throw new InvalidDataException("标签排序值越界");
                tags.Add(new Tag { Id = id, Name = name, Color = color, SortOrder = (int)sortOrder });
            }
        }

        var mappings = new List<(long ClipItemId, long TagId)>();
        var mappingSet = new HashSet<(long, long)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ClipItemId,TagId FROM clip_item_tags;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long clipId = ReadPositiveInteger(reader, 0, "clip_item_tags.ClipItemId");
                long tagId = ReadPositiveInteger(reader, 1, "clip_item_tags.TagId");
                if (!itemIds.Contains(clipId) || !tagIds.Contains(tagId))
                    throw new InvalidDataException("标签关系引用不存在的记录");
                if (!mappingSet.Add((clipId, tagId)))
                    throw new InvalidDataException("标签关系重复");
                mappings.Add((clipId, tagId));
            }
        }

        if (!usedPayloads.SetEquals(payloadRelativePaths))
        {
            string unused = payloadRelativePaths.FirstOrDefault(path => !usedPayloads.Contains(path)) ?? "unknown";
            throw new InvalidDataException($"归档包含未被数据库声明的 payload：{unused}");
        }

        return new BackupDatabaseSnapshot(items, tags, mappings);
    }

    private static SqliteConnection OpenUntrustedReadOnly(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA query_only=ON;
            PRAGMA trusted_schema=OFF;
            PRAGMA recursive_triggers=OFF;
            PRAGMA foreign_keys=ON;
            """;
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            bool sawOk = false;
            while (reader.Read())
            {
                string result = reader.GetString(0);
                if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("备份数据库 integrity_check 失败");
                sawOk = true;
            }
            if (!sawOk)
                throw new InvalidDataException("备份数据库 integrity_check 无结果");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
                throw new InvalidDataException("备份数据库 foreign_key_check 失败");
        }
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        var seenTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY name;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string type = reader.GetString(0);
                string name = reader.GetString(1);
                string tableName = reader.GetString(2);
                string? sql = reader.IsDBNull(3) ? null : reader.GetString(3);

                if (type.Equals("table", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Equals("sqlite_sequence", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!RequiredTables.ContainsKey(name))
                        throw new InvalidDataException($"备份数据库包含额外表：{name}");
                    if (sql?.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase) == true)
                        throw new InvalidDataException($"备份数据库包含虚拟表：{name}");
                    seenTables.Add(name);
                }
                else if (type.Equals("index", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!RequiredTables.ContainsKey(tableName))
                            throw new InvalidDataException($"未知自动索引：{name}");
                    }
                    else if (!RequiredIndexes.Contains(name))
                    {
                        throw new InvalidDataException($"备份数据库包含额外索引：{name}");
                    }
                    seenIndexes.Add(name);
                }
                else
                {
                    throw new InvalidDataException($"备份数据库包含不允许的 {type}：{name}");
                }
            }
        }

        if (!seenTables.SetEquals(RequiredTables.Keys))
            throw new InvalidDataException("备份数据库缺少必需表");
        if (!RequiredIndexes.All(seenIndexes.Contains))
            throw new InvalidDataException("备份数据库缺少必需索引");

        foreach ((string table, string[] requiredColumns) in RequiredTables)
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_xinfo(\"{table}\");";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                actual.Add(reader.GetString(1));
            if (!actual.SetEquals(requiredColumns))
                throw new InvalidDataException($"备份数据库表 {table} 的列集合不受支持");
        }
    }

    private static void ValidateClipRow(SqliteDataReader reader)
    {
        _ = ReadPositiveInteger(reader, reader.GetOrdinal("Id"), "clip_items.Id");
        long type = ReadInteger(reader, reader.GetOrdinal("Type"), "clip_items.Type");
        if (!Enum.IsDefined(typeof(ClipType), (int)type))
            throw new InvalidDataException("ClipType 非法");
        _ = ReadString(reader, reader.GetOrdinal("PreviewText"), "clip_items.PreviewText");
        ValidateNullableString(reader, "TextContent");
        ValidateNullableString(reader, "RefPath");
        ValidateNullableString(reader, "ThumbnailPath");
        ValidateNullableString(reader, "SourceApp");
        ValidateNullableString(reader, "SourceIconPath");

        string createdAt = ReadString(reader, reader.GetOrdinal("CreatedAt"), "clip_items.CreatedAt");
        if (!DateTime.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new InvalidDataException("CreatedAt 非法");
        ValidateBoolean(reader, "IsPinned");

        string contentHash = ReadString(reader, reader.GetOrdinal("ContentHash"), "clip_items.ContentHash");
        if (string.IsNullOrWhiteSpace(contentHash) || contentHash.Length > 1024)
            throw new InvalidDataException("ContentHash 非法");
        long size = ReadInteger(reader, reader.GetOrdinal("SizeBytes"), "clip_items.SizeBytes");
        if (size < 0)
            throw new InvalidDataException("SizeBytes 不能为负数");
        ValidateBoolean(reader, "IsDeleted");
        if (ReadInteger(reader, reader.GetOrdinal("IsDeleted"), "clip_items.IsDeleted") != 0)
            throw new InvalidDataException("备份快照不得包含已删除记录");
        if (!reader.IsDBNull(reader.GetOrdinal("DeletedAt")))
            throw new InvalidDataException("活动记录不得携带 DeletedAt");
        ValidateNullableString(reader, "OcrText");
        long ocrStatus = ReadInteger(reader, reader.GetOrdinal("OcrStatus"), "clip_items.OcrStatus");
        if (!Enum.IsDefined(typeof(OcrStatus), (int)ocrStatus))
            throw new InvalidDataException("OcrStatus 非法");
    }

    private static void ValidateItemPayloadSemantics(
        ClipItem item,
        string archiveRoot,
        IReadOnlySet<string> payloads,
        HashSet<string> usedPayloads)
    {
        string? refRelative = ResolveOptionalPayload(item.RefPath, payloads, usedPayloads, "RefPath");
        string? thumbRelative = ResolveOptionalPayload(item.ThumbnailPath, payloads, usedPayloads, "ThumbnailPath");
        string? iconRelative = ResolveOptionalPayload(item.SourceIconPath, payloads, usedPayloads, "SourceIconPath");

        if (iconRelative is not null && !iconRelative.StartsWith("icons/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SourceIconPath 必须映射到 icons/");
        if (thumbRelative is not null && !thumbRelative.StartsWith("thumbs/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ThumbnailPath 必须映射到 thumbs/");

        switch (item.Type)
        {
            case ClipType.Text:
            case ClipType.Code:
            case ClipType.Color:
                if (refRelative is not null || thumbRelative is not null)
                    throw new InvalidDataException("普通文本类记录不得携带受管文件路径");
                break;

            case ClipType.Url:
                if (refRelative is not null || thumbRelative is not null
                    || !Uri.TryCreate(item.TextContent, UriKind.Absolute, out Uri? uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidDataException("URL 记录必须是绝对 HTTP/HTTPS 且不得携带 payload");
                break;

            case ClipType.RichText:
                RequirePrefix(refRelative, "richtext/", "RichText RefPath");
                if (thumbRelative is not null)
                    throw new InvalidDataException("RichText 不得携带缩略图路径");
                break;

            case ClipType.Image:
                RequirePrefix(refRelative, "images/", "Image RefPath");
                break;

            case ClipType.File:
                RequirePrefix(refRelative, "files/manifests/", "File RefPath");
                ValidateFileManifest(archiveRoot, refRelative!, payloads, usedPayloads);
                break;

            default:
                throw new InvalidDataException("未知 ClipType");
        }
    }

    private static void ValidateFileManifest(
        string archiveRoot,
        string manifestRelative,
        IReadOnlySet<string> payloads,
        HashSet<string> usedPayloads)
    {
        string path = BackupPathPolicy.CombineUnderRoot(archiveRoot, "payloads/" + manifestRelative);
        ClipFileManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ClipFileManifest>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("文件 manifest 无法解析", ex);
        }

        if (manifest is null || manifest.Version != 1 || manifest.Entries.Count == 0)
            throw new InvalidDataException("文件 manifest 版本或内容无效");

        foreach (ClipFileManifestEntry entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalPath) || entry.SizeBytes < 0)
                throw new InvalidDataException("文件 manifest 的 OriginalPath/SizeBytes 无效");

            if (manifest.IsReferenceOnly)
            {
                if (!string.IsNullOrWhiteSpace(entry.StoredPath))
                    throw new InvalidDataException("仅引用文件不得携带 StoredPath");
                continue;
            }

            string? stored = ResolveOptionalPayload(entry.StoredPath, payloads, usedPayloads, "StoredPath");
            RequirePrefix(stored, "files/payloads/", "File StoredPath");
        }
    }

    private static string? ResolveOptionalPayload(
        string? databasePath,
        IReadOnlySet<string> payloads,
        HashSet<string> usedPayloads,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            return null;

        string normalized = databasePath.Replace('\\', '/');
        string? match = null;
        foreach (string payload in payloads)
        {
            if (normalized.Equals(payload, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith('/' + payload, StringComparison.OrdinalIgnoreCase))
            {
                if (match is not null && !match.Equals(payload, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{fieldName} 映射不唯一");
                match = payload;
            }
        }

        if (match is null)
            throw new InvalidDataException($"{fieldName} 未映射到归档 payload");
        usedPayloads.Add(match);
        return match;
    }

    private static void RequirePrefix(string? value, string prefix, string field)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{field} 必须映射到 {prefix}");
    }

    private static void ValidateNullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        if (!reader.IsDBNull(ordinal) && reader.GetValue(ordinal) is not string)
            throw new InvalidDataException($"{name} 类型无效");
    }

    private static void ValidateBoolean(SqliteDataReader reader, string name)
    {
        long value = ReadInteger(reader, reader.GetOrdinal(name), name);
        if (value is not 0 and not 1)
            throw new InvalidDataException($"{name} 必须为 0/1");
    }

    private static long ReadPositiveInteger(SqliteDataReader reader, int ordinal, string name)
    {
        long value = ReadInteger(reader, ordinal, name);
        if (value <= 0)
            throw new InvalidDataException($"{name} 必须为正整数");
        return value;
    }

    private static long ReadInteger(SqliteDataReader reader, int ordinal, string name)
    {
        object value = reader.GetValue(ordinal);
        if (value is not long integer)
            throw new InvalidDataException($"{name} 必须为 INTEGER");
        return integer;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal, string name)
    {
        object value = reader.GetValue(ordinal);
        if (value is not string text)
            throw new InvalidDataException($"{name} 必须为 TEXT");
        return text;
    }
}

internal sealed record BackupDatabaseSnapshot(
    List<ClipItem> Items,
    List<Tag> Tags,
    List<(long ClipItemId, long TagId)> TagMappings);
