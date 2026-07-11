using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Clipora.Services;

internal sealed class ClipFileManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public int Version { get; set; } = 1;

    public bool IsReferenceOnly { get; set; }

    /// <summary>惰性检测标记：仅引用项的原路径在最近一次检测中不可用。重启后从此字段恢复状态。</summary>
    public bool IsReferenceInvalid { get; set; }

    public List<ClipFileManifestEntry> Entries { get; set; } = new();

    public void Save(string path)
    {
        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static ClipFileManifest? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClipFileManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetAvailablePaths() =>
        Entries
            .Select(e => string.IsNullOrWhiteSpace(e.StoredPath) ? e.OriginalPath : e.StoredPath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .ToArray();
}

internal sealed class ClipFileManifestEntry
{
    public string OriginalPath { get; set; } = string.Empty;

    public string? StoredPath { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long SizeBytes { get; set; }
}
