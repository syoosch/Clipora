using System.IO;
using System.Security;
using Clipora.Models;

namespace Clipora.Services;

internal enum StorageMigrationPlanError
{
    None,
    Unavailable,
    InvalidParent,
    UnsupportedNetworkPath,
    SameOrNestedPath,
    SourceUnavailable,
    TargetExists,
    ReparsePoint,
    AccessDenied,
    InsufficientSpace,
    PendingExists,
    PlanChanged,
    Unknown,
}

internal sealed record StorageMigrationPlan(
    string SourceRoot,
    string SelectedParent,
    string TargetRoot,
    long SourceBytes,
    long RequiredBytes);

internal sealed record StorageMigrationPlanResult(
    bool Succeeded,
    StorageMigrationPlan? Plan,
    StorageMigrationPlanError Error,
    string? Detail);

internal sealed record StorageMigrationEnqueueResult(
    bool Succeeded,
    StorageMigrationPlan? Plan,
    Guid? MigrationId,
    StorageMigrationPlanError Error,
    string? Detail);

internal interface IStorageMigrationPlanner
{
    StorageMigrationPlanResult Plan(string? selectedParent);
    StorageMigrationEnqueueResult Enqueue(StorageMigrationPlan? plan);
}

internal interface IStorageMigrationQueueStore
{
    StorageMigrationState Read();
    void Enqueue(string targetRoot, Guid migrationId);
}

internal interface IStorageMigrationPlanFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    FileAttributes GetAttributes(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string directory);
    long GetFileLength(string path);
    void CreateProbe(string path);
    void DeleteFile(string path);
    bool IsNetworkPath(string path);
}

internal sealed class StorageMigrationPlanFileSystem : IStorageMigrationPlanFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly);
    public long GetFileLength(string path) => new FileInfo(path).Length;
    public void CreateProbe(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    public void DeleteFile(string path) => File.Delete(path);

    public bool IsNetworkPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            bool isExtendedLocal = full.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
            if (!isExtendedLocal)
                return true;
        }

        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
            return true;

        return new DriveInfo(root).DriveType == DriveType.Network;
    }
}

/// <summary>
/// 把用户选择的父目录转换为迁移计划，并在写入 pending 前进行完整复验。
/// 此服务只做确认前预检；迁移引擎仍是提交安全的权威校验层。
/// </summary>
internal sealed class StorageMigrationPlanService : IStorageMigrationPlanner
{
    private const string DatabaseFileName = "clipora.db";
    private const long MinimumHeadroomBytes = 64L * 1024 * 1024;

    private readonly string _sourceRoot;
    private readonly bool _canMigrate;
    private readonly IStorageMigrationQueueStore _queueStore;
    private readonly ISpaceProbe _spaceProbe;
    private readonly IStorageMigrationPlanFileSystem _fileSystem;

    public StorageMigrationPlanService(
        string sourceRoot,
        bool canMigrate,
        IStorageMigrationQueueStore queueStore,
        ISpaceProbe? spaceProbe = null,
        IStorageMigrationPlanFileSystem? fileSystem = null)
    {
        _sourceRoot = sourceRoot ?? throw new ArgumentNullException(nameof(sourceRoot));
        _canMigrate = canMigrate;
        _queueStore = queueStore ?? throw new ArgumentNullException(nameof(queueStore));
        _spaceProbe = spaceProbe ?? new DefaultSpaceProbe();
        _fileSystem = fileSystem ?? new StorageMigrationPlanFileSystem();
    }

    public StorageMigrationPlanResult Plan(string? selectedParent)
    {
        if (!_canMigrate)
            return FailPlan(StorageMigrationPlanError.Unavailable, "当前数据目录由开发环境或显式覆盖固定，不能迁移。");

        try
        {
            if (string.IsNullOrWhiteSpace(selectedParent)
                || !Path.IsPathFullyQualified(selectedParent))
            {
                return FailPlan(StorageMigrationPlanError.InvalidParent, "请选择一个完全限定的本地父目录。");
            }

            if (!Path.IsPathFullyQualified(_sourceRoot))
                return FailPlan(StorageMigrationPlanError.SourceUnavailable, "当前数据目录不是完全限定路径。");

            string sourceRoot = Path.GetFullPath(_sourceRoot);
            string parentRoot = Path.GetFullPath(selectedParent);
            string targetRoot = Path.GetFullPath(Path.Combine(parentRoot, "Clipora"));

            if (_fileSystem.IsNetworkPath(sourceRoot)
                || _fileSystem.IsNetworkPath(parentRoot)
                || _fileSystem.IsNetworkPath(targetRoot))
            {
                return FailPlan(StorageMigrationPlanError.UnsupportedNetworkPath, "数据目录迁移只支持本地磁盘。");
            }

            if (!_fileSystem.DirectoryExists(sourceRoot)
                || !_fileSystem.FileExists(Path.Combine(sourceRoot, DatabaseFileName)))
            {
                return FailPlan(StorageMigrationPlanError.SourceUnavailable, "当前数据目录不可用或缺少 clipora.db。");
            }

            if (!_fileSystem.DirectoryExists(parentRoot))
                return FailPlan(StorageMigrationPlanError.InvalidParent, "所选父目录不存在。");

            if (_fileSystem.DirectoryExists(targetRoot) || _fileSystem.FileExists(targetRoot))
                return FailPlan(StorageMigrationPlanError.TargetExists, "目标位置已存在 Clipora 文件或目录。");

            if (AreRelated(sourceRoot, targetRoot))
                return FailPlan(StorageMigrationPlanError.SameOrNestedPath, "当前目录与目标目录不能相同或互为父子目录。");

            if (IsReparsePoint(sourceRoot) || IsReparsePoint(parentRoot))
                return FailPlan(StorageMigrationPlanError.ReparsePoint, "当前目录或目标父目录是重解析点，不能迁移。");

            if (!TryCollectSourceFiles(sourceRoot, out List<string>? files, out bool foundReparsePoint, out string? treeError))
            {
                return FailPlan(
                    foundReparsePoint ? StorageMigrationPlanError.ReparsePoint : StorageMigrationPlanError.Unknown,
                    treeError ?? "无法安全枚举数据目录。");
            }

            StorageMigrationState state = _queueStore.Read();
            if (state.PendingRoot is not null || state.MigrationId is not null)
                return FailPlan(StorageMigrationPlanError.PendingExists, "已有存储迁移等待处理，不能覆盖。");
            if (!SamePath(state.ActiveRoot, sourceRoot))
                return FailPlan(StorageMigrationPlanError.PlanChanged, "当前活动数据目录已变化，请重新打开设置。");

            string probePath = Path.Combine(parentRoot, ".clipora-write-probe-" + Guid.NewGuid().ToString("N"));
            Exception? probeFailure = null;
            bool probeCreated = false;
            try
            {
                _fileSystem.CreateProbe(probePath);
                probeCreated = true;
            }
            catch (Exception ex)
            {
                probeFailure = ex;
            }
            finally
            {
                if (probeCreated)
                {
                    try { _fileSystem.DeleteFile(probePath); }
                    catch (Exception ex) { probeFailure ??= ex; }
                }
            }

            if (probeFailure is not null)
                return FailPlan(StorageMigrationPlanError.AccessDenied, $"目标父目录不可写: {probeFailure.Message}");

            long sourceBytes = 0;
            try
            {
                foreach (string file in files!)
                {
                    if (IsRootWalOrShm(sourceRoot, file))
                        continue;
                    sourceBytes = checked(sourceBytes + _fileSystem.GetFileLength(file));
                }
            }
            catch (Exception ex)
            {
                return FailPlan(StorageMigrationPlanError.Unknown, $"无法计算当前数据大小: {ex.Message}");
            }

            long requiredBytes;
            try
            {
                long headroom = Math.Max(MinimumHeadroomBytes, sourceBytes / 10);
                requiredBytes = checked(sourceBytes + headroom);
            }
            catch (OverflowException ex)
            {
                return FailPlan(StorageMigrationPlanError.Unknown, $"数据大小超出可计算范围: {ex.Message}");
            }

            if (!_spaceProbe.HasSufficientSpace(parentRoot, requiredBytes))
                return FailPlan(StorageMigrationPlanError.InsufficientSpace, $"目标磁盘空间不足，需要至少 {requiredBytes} 字节。");

            return new StorageMigrationPlanResult(
                true,
                new StorageMigrationPlan(sourceRoot, parentRoot, targetRoot, sourceBytes, requiredBytes),
                StorageMigrationPlanError.None,
                null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return FailPlan(StorageMigrationPlanError.AccessDenied, $"无法访问所选目录: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or SecurityException)
        {
            return FailPlan(StorageMigrationPlanError.InvalidParent, $"目录预检失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            return FailPlan(StorageMigrationPlanError.Unknown, $"目录预检失败: {ex.Message}");
        }
    }

    public StorageMigrationEnqueueResult Enqueue(StorageMigrationPlan? plan)
    {
        if (plan is null)
            return FailEnqueue(StorageMigrationPlanError.PlanChanged, "迁移计划为空，请重新选择目录。");

        try
        {
            if (!IsCanonicalPlanPath(plan.SourceRoot)
                || !IsCanonicalPlanPath(plan.SelectedParent)
                || !IsCanonicalPlanPath(plan.TargetRoot))
            {
                return FailEnqueue(StorageMigrationPlanError.PlanChanged, "迁移计划路径无效，请重新选择目录。");
            }

            StorageMigrationPlanResult freshResult = Plan(plan.SelectedParent);
            if (!freshResult.Succeeded)
            {
                StorageMigrationPlanError error = freshResult.Error == StorageMigrationPlanError.PendingExists
                    ? StorageMigrationPlanError.PendingExists
                    : StorageMigrationPlanError.PlanChanged;
                return FailEnqueue(error, freshResult.Detail ?? "迁移计划已变化，请重新确认。");
            }

            StorageMigrationPlan fresh = freshResult.Plan!;
            if (!PlansEqual(plan, fresh))
                return FailEnqueue(StorageMigrationPlanError.PlanChanged, "数据目录内容或目标状态已变化，请重新确认。");

            StorageMigrationState before = _queueStore.Read();
            if (before.PendingRoot is not null || before.MigrationId is not null)
                return FailEnqueue(StorageMigrationPlanError.PendingExists, "已有存储迁移等待处理，不能覆盖。");
            if (!SamePath(before.ActiveRoot, fresh.SourceRoot))
                return FailEnqueue(StorageMigrationPlanError.PlanChanged, "当前活动数据目录已变化，请重新确认。");

            Guid migrationId = Guid.NewGuid();
            _queueStore.Enqueue(fresh.TargetRoot, migrationId);

            StorageMigrationState after = _queueStore.Read();
            if (!SamePath(after.ActiveRoot, fresh.SourceRoot)
                || !SamePath(after.PendingRoot, fresh.TargetRoot)
                || after.MigrationId != migrationId)
            {
                return FailEnqueue(StorageMigrationPlanError.Unknown, "迁移状态写后校验失败；已保留现场供启动恢复处理。");
            }

            return new StorageMigrationEnqueueResult(
                true,
                fresh,
                migrationId,
                StorageMigrationPlanError.None,
                null);
        }
        catch (Exception ex)
        {
            return FailEnqueue(StorageMigrationPlanError.Unknown, $"迁移入队失败: {ex.Message}");
        }
    }

    private bool TryCollectSourceFiles(
        string sourceRoot,
        out List<string>? files,
        out bool foundReparsePoint,
        out string? error)
    {
        files = new List<string>();
        foundReparsePoint = false;
        error = null;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceRoot);

        try
        {
            while (pendingDirectories.Count > 0)
            {
                string directory = pendingDirectories.Pop();
                foreach (string entry in _fileSystem.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = _fileSystem.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        foundReparsePoint = true;
                        error = $"数据目录包含重解析点: {entry}";
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                        pendingDirectories.Push(entry);
                    else
                        files.Add(entry);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"无法安全枚举数据目录: {ex.Message}";
            return false;
        }
    }

    private bool IsReparsePoint(string path) =>
        (_fileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsRootWalOrShm(string sourceRoot, string file)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!SamePath(parent, sourceRoot))
            return false;

        string name = Path.GetFileName(file);
        return string.Equals(name, "clipora.db-wal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "clipora.db-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreRelated(string left, string right) =>
        SamePath(left, right) || IsStrictlyUnder(left, right) || IsStrictlyUnder(right, left);

    private static bool IsStrictlyUnder(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.GetFullPath(root);
        if (SamePath(fullCandidate, fullRoot))
            return false;
        return fullCandidate.StartsWith(EnsureTrailingSeparator(fullRoot), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCanonicalPlanPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        try { return SamePath(path, Path.GetFullPath(path)); }
        catch { return false; }
    }

    private static bool PlansEqual(StorageMigrationPlan left, StorageMigrationPlan right) =>
        SamePath(left.SourceRoot, right.SourceRoot)
        && SamePath(left.SelectedParent, right.SelectedParent)
        && SamePath(left.TargetRoot, right.TargetRoot)
        && left.SourceBytes == right.SourceBytes
        && left.RequiredBytes == right.RequiredBytes;

    private static StorageMigrationPlanResult FailPlan(StorageMigrationPlanError error, string detail) =>
        new(false, null, error, detail);

    private static StorageMigrationEnqueueResult FailEnqueue(StorageMigrationPlanError error, string detail) =>
        new(false, null, null, error, detail);
}
