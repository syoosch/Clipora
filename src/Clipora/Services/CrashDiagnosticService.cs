using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Clipora.Services;

/// <summary>Release 只落脱敏元数据；Debug 仅在合法隔离数据根下落完整异常。</summary>
internal sealed class CrashDiagnosticService
{
    private const int MaxFiles = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly bool _releaseMode;
    private readonly string? _diagnosticsDirectory;
    private readonly string _legacyCrashPath;

    internal CrashDiagnosticService(
        bool releaseMode,
        string? developmentRootOverride = null,
        string? releaseRootOverride = null,
        string? legacyCrashPathOverride = null)
    {
        _releaseMode = releaseMode;
        _legacyCrashPath = legacyCrashPathOverride
            ?? Path.Combine(Path.GetTempPath(), "clipora-crash.txt");

        if (releaseMode)
        {
            string releaseRoot = releaseRootOverride
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipora");
            _diagnosticsDirectory = Path.Combine(Path.GetFullPath(releaseRoot), "diagnostics");
        }
        else
        {
            string? developmentRoot = developmentRootOverride
                ?? Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
            _diagnosticsDirectory = TryResolveDevelopmentDiagnosticsDirectory(developmentRoot);
        }
    }

    internal void Initialize()
    {
        TryDeleteLegacyCrashFile();
        Cleanup();
    }

    internal string? WriteException(Exception exception, string stableErrorCode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Write(stableErrorCode, exception, exception.ToString());
    }

    internal string? WriteUnhandledObject(object? value, string stableErrorCode)
    {
        Exception? exception = value as Exception;
        string debugDetail = value?.ToString() ?? "unknown";
        return Write(stableErrorCode, exception, debugDetail);
    }

    internal string? WriteOperationalError(
        string stableErrorCode,
        Exception? exception = null,
        string? debugDetail = null) =>
        Write(stableErrorCode, exception, debugDetail ?? exception?.ToString() ?? stableErrorCode);

    internal void Cleanup()
    {
        if (string.IsNullOrEmpty(_diagnosticsDirectory) || !Directory.Exists(_diagnosticsDirectory))
            return;
        try
        {
            var files = new DirectoryInfo(_diagnosticsDirectory)
                .GetFiles("diagnostic-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            DateTime cutoff = DateTime.UtcNow - MaxAge;
            for (int index = 0; index < files.Count; index++)
            {
                if (files[index].LastWriteTimeUtc < cutoff || index >= MaxFiles)
                {
                    try { files[index].Delete(); } catch { }
                }
            }
        }
        catch { }
    }

    private string? Write(string stableErrorCode, Exception? exception, string debugDetail)
    {
        if (string.IsNullOrWhiteSpace(_diagnosticsDirectory))
            return null;
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);
            string errorId = Guid.NewGuid().ToString("N")[..12];
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string extension = _releaseMode ? ".json" : ".txt";
            string path = Path.Combine(_diagnosticsDirectory, $"diagnostic-{timestamp}-{errorId}{extension}");

            if (_releaseMode)
                WriteReleaseRecord(path, errorId, stableErrorCode, exception);
            else
                File.WriteAllText(path, debugDetail, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Cleanup();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteReleaseRecord(
        string path, string errorId, string stableErrorCode, Exception? exception)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("Utc", DateTime.UtcNow);
        writer.WriteString("AppVersion", GetAppVersion());
        writer.WriteString("ErrorId", errorId);
        writer.WriteString("ErrorCode", NormalizeStableErrorCode(stableErrorCode));
        writer.WriteString("ExceptionType", exception?.GetType().FullName ?? "none");
        writer.WriteNumber("HResult", exception?.HResult ?? 0);
        writer.WriteString("Component", FindFirstCliporaComponent(exception));
        writer.WriteEndObject();
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string NormalizeStableErrorCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNKNOWN";
        string normalized = new(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .Select(char.ToUpperInvariant)
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? "UNKNOWN" : normalized;
    }

    private static string FindFirstCliporaComponent(Exception? exception)
    {
        if (exception is null)
            return "unknown";
        try
        {
            StackFrame? frame = new StackTrace(exception, fNeedFileInfo: false)
                .GetFrames()
                .FirstOrDefault(candidate =>
                    candidate.GetMethod()?.DeclaringType?.Namespace?.StartsWith(
                        "Clipora", StringComparison.Ordinal) == true);
            return frame?.GetMethod()?.DeclaringType?.FullName ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetAppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    private static string? TryResolveDevelopmentDiagnosticsDirectory(string? developmentRoot)
    {
        if (string.IsNullOrWhiteSpace(developmentRoot))
            return null;
        try
        {
            if (!Path.IsPathFullyQualified(developmentRoot))
                return null;
            return Path.Combine(Path.GetFullPath(developmentRoot), "diagnostics");
        }
        catch
        {
            return null;
        }
    }

    private void TryDeleteLegacyCrashFile()
    {
        try { File.Delete(_legacyCrashPath); } catch { }
    }
}
