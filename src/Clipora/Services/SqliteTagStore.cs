using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary><see cref="ITagStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteTagStore : ITagStore
{
    private readonly Database _db;

    public SqliteTagStore(Database db) => _db = db;

    public IReadOnlyList<Tag> List()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Color, SortOrder FROM tags ORDER BY SortOrder, Name;";

        var list = new List<Tag>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Tag
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Color = r.GetString(2),
                SortOrder = r.GetInt32(3),
            });
        }
        return list;
    }

    public long Create(string name, string color)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tags (Name, Color, SortOrder) " +
            "VALUES ($n, $c, (SELECT IFNULL(MAX(SortOrder), 0) + 1 FROM tags));";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", color);
        cmd.ExecuteNonQuery();

        using var idCmd = c.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(idCmd.ExecuteScalar());
    }

    public void Rename(long id, string name) =>
        Exec("UPDATE tags SET Name = $n WHERE Id = $id;", ("$n", name), ("$id", id));

    public void SetColor(long id, string color) =>
        Exec("UPDATE tags SET Color = $c WHERE Id = $id;", ("$c", color), ("$id", id));

    public void Reorder(long id, int sortOrder) =>
        Exec("UPDATE tags SET SortOrder = $s WHERE Id = $id;", ("$s", sortOrder), ("$id", id));

    public void Delete(long id) =>
        Exec("DELETE FROM tags WHERE Id = $id;", ("$id", id));

    public IReadOnlyList<long> GetTagIds(long clipId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TagId FROM clip_item_tags WHERE ClipItemId = $id;";
        cmd.Parameters.AddWithValue("$id", clipId);

        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(r.GetInt64(0));
        return list;
    }

    public IReadOnlyDictionary<long, IReadOnlyList<long>> GetAllTagAssignments()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ClipItemId, TagId FROM clip_item_tags;";

        var map = new Dictionary<long, List<long>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long clipId = r.GetInt64(0);
            if (!map.TryGetValue(clipId, out List<long>? list))
                map[clipId] = list = new List<long>();
            list.Add(r.GetInt64(1));
        }

        var result = new Dictionary<long, IReadOnlyList<long>>(map.Count);
        foreach (var kv in map)
            result[kv.Key] = kv.Value;
        return result;
    }

    public void Assign(long clipId, long tagId) =>
        Exec("INSERT OR IGNORE INTO clip_item_tags (ClipItemId, TagId) VALUES ($c, $t);", ("$c", clipId), ("$t", tagId));

    public void Unassign(long clipId, long tagId) =>
        Exec("DELETE FROM clip_item_tags WHERE ClipItemId = $c AND TagId = $t;", ("$c", clipId), ("$t", tagId));

    private void Exec(string sql, params (string name, object value)[] parameters)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
