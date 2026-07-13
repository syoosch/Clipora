using System.IO;

namespace Clipora.Services;

/// <summary>
/// 备份归档、manifest、journal 与最终受管路径共享的 Windows 路径策略。
/// 所有返回的相对路径均使用正斜杠，且已经过大小写不敏感语义下的规范化检查。
/// </summary>
internal static class BackupPathPolicy
{
    private static readonly string[] ManagedPrefixes =
    [
        "images/",
        "thumbs/",
        "icons/",
        "richtext/",
        "files/payloads/",
        "files/manifests/",
    ];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    internal static bool TryNormalizeManagedRelativePath(
        string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (!TryNormalizeRelativePath(path, out string candidate, out error))
            return false;

        if (!ManagedPrefixes.Any(prefix => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || ManagedPrefixes.Any(prefix => candidate.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            error = "路径不属于允许的受管目录";
            return false;
        }

        if (ManagedPrefixes.Any(prefix => candidate.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            error = "受管路径必须指向文件";
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    internal static bool TryNormalizeArchiveEntry(
        string? path, bool isDirectory, out string normalized, out string error)
    {
        normalized = string.Empty;
        string? trimmed = isDirectory ? path?.TrimEnd('/', '\\') : path;
        if (!TryNormalizeRelativePath(trimmed, out string candidate, out error))
            return false;

        if (!isDirectory)
        {
            if (candidate.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("clipora.db", StringComparison.OrdinalIgnoreCase))
            {
                normalized = candidate.ToLowerInvariant();
                error = string.Empty;
                return true;
            }

            const string payloadPrefix = "payloads/";
            if (!candidate.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase)
                || !TryNormalizeManagedRelativePath(candidate[payloadPrefix.Length..], out string managed, out error))
            {
                if (string.IsNullOrEmpty(error))
                    error = "归档只允许 manifest.json、clipora.db 与 payloads/<受管路径>";
                return false;
            }

            normalized = payloadPrefix + managed;
            error = string.Empty;
            return true;
        }

        if (!IsAllowedArchiveDirectory(candidate))
        {
            error = "归档目录不在允许范围内";
            return false;
        }

        normalized = candidate;
        error = string.Empty;
        return true;
    }

    internal static string CombineUnderRoot(string root, string normalizedRelativePath)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string boundary = fullRoot + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径越出目标根目录");
        return candidate;
    }

    internal static bool TryGetManagedRelativePath(
        string root, string? fullPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        try
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string boundary = fullRoot + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(fullPath);
            if (!candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                return false;

            string relative = Path.GetRelativePath(fullRoot, candidate).Replace('\\', '/');
            if (!TryNormalizeManagedRelativePath(relative, out string normalized, out _))
                return false;

            relativePath = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasReparsePointInExistingAncestors(string root, string targetPath)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(targetPath);
        string boundary = fullRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            return true;

        string? current = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        while (!string.IsNullOrEmpty(current)
            && current.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    internal static void SafeDeleteTree(string parentRoot, string directoryPath)
    {
        string fullParent = Path.GetFullPath(parentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullDirectory = Path.GetFullPath(directoryPath);
        string boundary = fullParent + Path.DirectorySeparatorChar;
        if (!fullDirectory.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
            || fullDirectory.Equals(fullParent, StringComparison.OrdinalIgnoreCase))
            return;

        DeleteTreeWithoutFollowingReparsePoints(fullDirectory);
    }

    private static bool TryNormalizeRelativePath(string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }

        string candidate = path.Replace('\\', '/');
        if (candidate.StartsWith('/')
            || candidate.StartsWith("//", StringComparison.Ordinal)
            || (candidate.Length >= 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':')
            || Path.IsPathRooted(candidate))
        {
            error = "不允许绝对、UNC 或盘符相对路径";
            return false;
        }

        string[] segments = candidate.Split('/');
        if (segments.Length == 0)
        {
            error = "路径为空";
            return false;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                error = "不允许空段或点段";
                return false;
            }
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                error = "路径段不得以空格或点结尾";
                return false;
            }
            if (segment.Contains(':') || segment.IndexOfAny(invalid) >= 0 || segment.Any(c => char.IsControl(c)))
            {
                error = "路径包含 ADS 冒号或非法字符";
                return false;
            }

            string deviceStem = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(deviceStem))
            {
                error = "路径包含 Windows 设备名";
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static bool IsAllowedArchiveDirectory(string candidate)
    {
        if (candidate.Equals("payloads", StringComparison.OrdinalIgnoreCase))
            return true;

        string managedCandidate = candidate.StartsWith("payloads/", StringComparison.OrdinalIgnoreCase)
            ? candidate["payloads/".Length..]
            : string.Empty;
        if (managedCandidate.Length == 0)
            return false;

        return ManagedPrefixes.Any(prefix =>
        {
            string directory = prefix.TrimEnd('/');
            return directory.Equals(managedCandidate, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(managedCandidate + '/', StringComparison.OrdinalIgnoreCase)
                || managedCandidate.StartsWith(directory + '/', StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        FileAttributes attributes = File.GetAttributes(directoryPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directoryPath);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            try
            {
                FileAttributes entryAttributes = File.GetAttributes(entry);
                if ((entryAttributes & FileAttributes.Directory) != 0)
                {
                    if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                        Directory.Delete(entry);
                    else
                        DeleteTreeWithoutFollowingReparsePoints(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch
            {
                // best-effort；调用方会在需要时保留 journal 供下次恢复。
            }
        }

        try { Directory.Delete(directoryPath); } catch { }
    }
}
