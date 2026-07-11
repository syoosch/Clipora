namespace Clipora.Models;

/// <summary>备份/导入的阶段，供进度报告。</summary>
public enum BackupPhase
{
    Preparing,
    CopyingDatabase,
    CopyingFiles,
    Packaging,
    Validating,
    Merging,
    Finalizing,
}

/// <summary>备份/导入进度快照。</summary>
public readonly record struct BackupProgress(BackupPhase Phase, int Done, int Total);

/// <summary>导入前预览（InspectAsync 返回值）。</summary>
public readonly record struct BackupPreview(
    bool Compatible,
    int FormatVersion,
    int ItemCount,
    DateTime CreatedAtUtc,
    string? Incompatibility);

/// <summary>导出结果。</summary>
public readonly record struct BackupExportResult(bool Ok, int ItemCount, long Bytes, string? Error);

/// <summary>导入结果。</summary>
public readonly record struct BackupImportResult(bool Ok, int Imported, int Skipped, string? Error);
