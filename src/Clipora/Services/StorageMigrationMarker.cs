using System;
using System.IO;
using System.Text.Json;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>.clipora-migration.json marker 的读写与所有权校验。</summary>
internal static class StorageMigrationMarker
{
    private const string MarkerFileName = ".clipora-migration.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>规范化路径下的 marker 路径。</summary>
    public static string GetMarkerPath(string root) => Path.Combine(root, MarkerFileName);

    /// <summary>读取并反序列化 marker；不存在或损坏时返回 null。</summary>
    public static StorageMigrationMarkerData? Read(string root)
    {
        string path = GetMarkerPath(root);
        try
        {
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StorageMigrationMarkerData>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写入 marker：先写临时文件，再 replace/move 到最终位置，禁止半写 JSON。</summary>
    public static void Write(string root, StorageMigrationMarkerData data)
    {
        string finalPath = GetMarkerPath(root);
        string tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>更新 marker 的阶段字段，原子写入。</summary>
    public static void UpdatePhase(string root, StorageMigrationPhase phase, StorageMigrationMarkerData data)
    {
        data.Phase = phase.ToString();
        Write(root, data);
    }

    /// <summary>
    /// 校验 staging/final-target 是否是 app-owned：marker 存在、SchemaVersion==1、
    /// MigrationId 匹配（Guid 精确比对）、规范化 source/target 与 marker 一致。
    /// </summary>
    public static bool IsAppOwned(
        string root,
        Guid migrationId,
        string expectedSource,
        string expectedTarget,
        out StorageMigrationMarkerData? marker)
    {
        marker = Read(root);
        if (marker is null)
            return false;
        if (marker.SchemaVersion != 1)
            return false;
        if (!Guid.TryParse(marker.MigrationId, out Guid markerId) || markerId != migrationId)
            return false;

        // 规范化比较 source/target
        try
        {
            if (!string.Equals(
                    Path.GetFullPath(marker.SourceRoot ?? string.Empty),
                    Path.GetFullPath(expectedSource),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(
                    Path.GetFullPath(marker.TargetRoot ?? string.Empty),
                    Path.GetFullPath(expectedTarget),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }
}
