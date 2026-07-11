using System.Diagnostics;
using System.IO;
using Clipora.Abstractions;

namespace Clipora.Services;

/// <summary>启动迁移完成进程的最小 seam；自检使用 spy，生产实现才调用 Process.Start。</summary>
internal interface IStorageMigrationProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class SystemStorageMigrationProcessLauncher : IStorageMigrationProcessLauncher
{
    public void Start(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("无法启动存储迁移完成进程。");
    }
}

/// <summary>
/// 验证迁移重启请求，并在 App 完成资源释放后最多启动一次迁移完成进程。
/// 不入队、不执行迁移、不清理 pending。
/// </summary>
internal sealed class StorageMigrationRestartCoordinator
{
    internal const string CompletionArgument = "--complete-storage-migration";

    private readonly IStorageMigrationStateStore _stateStore;
    private readonly IStorageMigrationProcessLauncher _launcher;
    private readonly Func<string?> _processPathProvider;
    private readonly string _workingDirectory;
    private readonly bool _canMigrate;
    private readonly bool _isReleaseBuild;
    private Guid? _requestedMigrationId;
    private bool _launchAttempted;

    internal StorageMigrationRestartCoordinator(
        IStorageMigrationStateStore stateStore,
        IStorageMigrationProcessLauncher launcher,
        Func<string?> processPathProvider,
        string workingDirectory,
        bool canMigrate,
        bool isReleaseBuild)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _processPathProvider = processPathProvider ?? throw new ArgumentNullException(nameof(processPathProvider));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _canMigrate = canMigrate;
        _isReleaseBuild = isReleaseBuild;
    }

    public static StorageMigrationRestartCoordinator CreateProduction(
        IStorageMigrationStateStore stateStore,
        bool canMigrate,
        bool isReleaseBuild) =>
        new(
            stateStore,
            new SystemStorageMigrationProcessLauncher(),
            () => Environment.ProcessPath,
            AppContext.BaseDirectory,
            canMigrate,
            isReleaseBuild);

    /// <summary>只接受与当前 pending 完全匹配的 Release/安装版重启请求。</summary>
    public bool TryRequest(Guid migrationId, out string? error)
    {
        error = null;

        if (!_isReleaseBuild)
        {
            error = "Debug 构建禁止请求存储迁移重启。";
            return false;
        }

        if (!_canMigrate)
        {
            error = "当前数据目录由开发环境或显式覆盖固定，禁止迁移。";
            return false;
        }

        if (migrationId == Guid.Empty)
        {
            error = "MigrationId 不能为空。";
            return false;
        }

        if (_requestedMigrationId is Guid acceptedId)
        {
            if (acceptedId == migrationId)
                return true;

            error = "已有其他存储迁移重启请求。";
            return false;
        }

        try
        {
            var state = _stateStore.Read();
            if (state.PendingRoot is null || state.MigrationId != migrationId)
            {
                error = "迁移重启请求与当前 pending 状态不匹配。";
                return false;
            }

            _requestedMigrationId = migrationId;
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法验证迁移重启请求: {ex.Message}";
            return false;
        }
    }

    /// <summary>App 退出资源释放完成后调用；每个已接受请求最多尝试启动一次。</summary>
    public bool TryLaunchAfterExit(out string? error)
    {
        error = null;

        if (_requestedMigrationId is null || _launchAttempted)
            return false;

        _launchAttempted = true;

        if (!_isReleaseBuild || !_canMigrate)
        {
            error = "当前运行身份不允许启动存储迁移完成进程。";
            return false;
        }

        try
        {
            string? executablePath = _processPathProvider();
            if (string.IsNullOrWhiteSpace(executablePath)
                || !Path.IsPathFullyQualified(executablePath)
                || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(executablePath))
            {
                error = "当前 Clipora 可执行文件路径无效，无法启动迁移完成进程。";
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(executablePath),
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(CompletionArgument);

            _launcher.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            error = $"启动存储迁移完成进程失败: {ex.Message}";
            return false;
        }
    }
}
