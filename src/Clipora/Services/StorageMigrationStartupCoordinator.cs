using System;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>启动恢复的处置结果。</summary>
internal enum StorageMigrationStartupAction
{
    /// <summary>无 pending 或 !canMigrate：按原 Root 正常启动。</summary>
    None,

    /// <summary>迁移完成或崩溃恢复成功：应按新 Root（target）启动。</summary>
    Completed,

    /// <summary>迁移/恢复失败：按原 Root（source）启动并提示失败。</summary>
    Failed,
}

/// <summary><see cref="StorageMigrationStartupCoordinator.ProcessPending"/> 的返回。</summary>
internal sealed record StorageMigrationStartupResult(
    StorageMigrationStartupAction Action,
    string ActiveRoot,
    StorageMigrationError Error,
    string? Detail,
    string? SourceRoot = null,
    string? TargetRoot = null);

/// <summary>
/// 启动迁移恢复协调器。在构造 <c>AppPaths</c>/<c>Database</c> 之前调用，
/// 根据注册表 pending 状态判断崩溃形态并驱动引擎执行或恢复。
/// 是 3a-1（<see cref="IStorageMigrationStateStore"/>）与 2e（<see cref="IStorageMigrationEngine"/>）的薄编排层。
/// </summary>
/// <remarks>
/// 不构造 AppPaths/Database、不写注册表（引擎经注入的 state store 提交）、不创建目录（引擎负责）。
/// 3a-3 的组合根在协调器返回后再 <c>new AppPaths()</c>（成功时自然解析到新 Root）。
///
/// 崩溃形态 → 引擎请求映射（冻结，对齐 <c>069 §3</c>）：
/// <list type="bullet">
/// <item>Form A（已提交残留）：<c>ActiveRoot==PendingRoot</c> → source=<c>LastSourceRoot ?? ActiveRoot</c></item>
/// <item>Form B/C（未提交）：<c>ActiveRoot!=PendingRoot</c> → source=<c>ActiveRoot</c></item>
/// </list>
/// </remarks>
internal sealed class StorageMigrationStartupCoordinator
{
    private readonly IStorageMigrationStateStore _stateStore;
    private readonly IStorageMigrationEngine _engine;

    /// <summary>
    /// <param name="stateStore">迁移状态持久化（生产 <see cref="RegistryStorageMigrationStateStore"/>，自检内存/隔离注册表）。</param>
    /// <param name="engine">迁移纯引擎（<see cref="StorageMigrationEngine"/>）。</param>
    /// </summary>
    public StorageMigrationStartupCoordinator(
        IStorageMigrationStateStore stateStore,
        IStorageMigrationEngine engine)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>
    /// 在构造 AppPaths/Database 之前调用：处理任何 pending 迁移。
    /// 不抛：所有异常转成 <c>Failed</c> 结果。
    /// </summary>
    /// <param name="canMigrate">来自 <c>AppPaths.CanMigrate</c>（env/override 为 false → 立即 None，不读 state）。</param>
    /// <param name="progress">可选的迁移进度回调（引擎隔离回调异常，不破坏迁移安全）。</param>
    public StorageMigrationStartupResult ProcessPending(
        bool canMigrate,
        IProgress<StorageMigrationProgress>? progress = null)
    {
        StorageMigrationState? knownState = null;
        try
        {
            // Gate: env/override 完全忽略 locator（057 D1），不作为迁移候选
            if (!canMigrate)
                return new StorageMigrationStartupResult(
                    StorageMigrationStartupAction.None,
                    string.Empty,
                    StorageMigrationError.None,
                    "canMigrate=false（env/override 模式不迁移）");

            StorageMigrationState state = _stateStore.Read();
            knownState = state;

            // 无 pending → 正常启动
            if (state.PendingRoot is null || state.MigrationId is null)
                return new StorageMigrationStartupResult(
                    StorageMigrationStartupAction.None,
                    state.ActiveRoot,
                    StorageMigrationError.None,
                    null);

            string target = state.PendingRoot;
            Guid id = state.MigrationId.Value;

            // 崩溃形态 → 引擎请求映射（冻结，069 §3）
            string source;
            if (string.Equals(state.ActiveRoot, state.PendingRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Form A：已提交残留（DataRoot==PendingRoot==target）
                // 源路径来自 CommitTarget 先写的 LastSourceRoot；缺失时回退 ActiveRoot
                source = state.LastSourceRoot ?? state.ActiveRoot;
            }
            else
            {
                // Form B（已提升未提交）或 Form C（已入队未提升）
                source = state.ActiveRoot;
            }

            var request = new StorageMigrationRequest(source, target, id);
            StorageMigrationResult result = _engine.Execute(request, progress);

            if (result.Succeeded)
                return new StorageMigrationStartupResult(
                    StorageMigrationStartupAction.Completed,
                    result.ActiveRoot,         // 引擎成功后 ActiveRoot == target
                    StorageMigrationError.None,
                    null,
                    source,
                    target);

            // 引擎失败 → 按原 Root 启动
            return new StorageMigrationStartupResult(
                StorageMigrationStartupAction.Failed,
                state.ActiveRoot,             // 仍指向 source
                result.Error,
                result.Detail,
                source,
                target);
        }
        catch (Exception ex)
        {
            // 绝不抛异常：所有异常转 Failed（069 §2）
            string fallbackRoot = knownState?.ActiveRoot ?? string.Empty;
            string? exceptionSource = null;
            string? exceptionTarget = null;
            try
            {
                StorageMigrationState s = knownState ?? _stateStore.Read();
                fallbackRoot = s.ActiveRoot;
                // best-effort：存在完整 pending 时按同一映射规则返回 source/target
                if (s.PendingRoot is not null && s.MigrationId is not null)
                {
                    exceptionTarget = s.PendingRoot;
                    exceptionSource = string.Equals(s.ActiveRoot, s.PendingRoot, StringComparison.OrdinalIgnoreCase)
                        ? (s.LastSourceRoot ?? s.ActiveRoot)
                        : s.ActiveRoot;
                }
            }
            catch { /* state store 自身故障，空字符串由 3a-3 的 AppPaths 兜底 */ }

            return new StorageMigrationStartupResult(
                StorageMigrationStartupAction.Failed,
                fallbackRoot,
                StorageMigrationError.Unknown,
                $"启动迁移协调器异常: {ex.Message}",
                exceptionSource,
                exceptionTarget);
        }
    }
}
