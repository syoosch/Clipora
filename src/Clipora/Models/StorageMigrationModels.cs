namespace Clipora.Models;

/// <summary>迁移引擎的输入。</summary>
public sealed record StorageMigrationRequest(
    string SourceRoot,
    string TargetRoot,
    Guid MigrationId);

/// <summary>迁移阶段。</summary>
public enum StorageMigrationPhase
{
    Validating,
    Checkpointing,
    Copying,
    Rebasing,
    Verifying,
    Promoting,
    Switching,
    Completed,
    Failed,
}

/// <summary>迁移进度报告。</summary>
public sealed record StorageMigrationProgress(
    StorageMigrationPhase Phase,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes);

/// <summary>受控迁移错误码。引擎 API 不得向上泄漏裸 IOException / SqliteException / UnauthorizedAccessException / ArgumentException。</summary>
public enum StorageMigrationError
{
    None,
    InvalidRequest,
    InvalidSource,
    InvalidTarget,
    InsufficientSpace,
    CheckpointFailed,
    CopyFailed,
    UnsafePath,
    RebaseFailed,
    VerificationFailed,
    PromotionFailed,
    SwitchFailed,
    StateMismatch,
    Unknown,
}

/// <summary>迁移引擎的受控结果。</summary>
public sealed record StorageMigrationResult(
    bool Succeeded,
    StorageMigrationPhase Phase,
    StorageMigrationError Error,
    string ActiveRoot,
    bool TargetWasPromoted,
    string? Detail);

/// <summary>迁移状态存储的读写模型。</summary>
public sealed record StorageMigrationState(
    string ActiveRoot,
    string? PendingRoot,
    Guid? MigrationId,
    string? LastSourceRoot);

/// <summary>故障注入点。</summary>
public enum StorageMigrationFaultPoint
{
    BeforeCheckpoint,
    AfterCheckpoint,
    AfterMarkerCreated,
    DuringCopy,
    AfterCopy,
    AfterRebase,
    AfterVerify,
    BeforePromote,
    AfterPromoteBeforeSwitch,
    DuringSwitch,
}

/// <summary>marker 文件持久化的数据。</summary>
public sealed class StorageMigrationMarkerData
{
    public int SchemaVersion { get; set; } = 1;

    public string? MigrationId { get; set; }

    public string? SourceRoot { get; set; }

    public string? TargetRoot { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? Phase { get; set; }
}
