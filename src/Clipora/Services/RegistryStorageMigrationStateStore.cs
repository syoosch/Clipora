using System;
using System.IO;
using System.Security;
using Clipora.Abstractions;
using Clipora.Models;
using Microsoft.Win32;

namespace Clipora.Services;

/// <summary>
/// 生产注册表迁移状态存储，实现 <see cref="IStorageMigrationStateStore"/>。
/// 读写 <c>HKCU\Software\Clipora\Storage</c> 下的控制值；所有注册表访问失败统一转为受控
/// <see cref="StorageLocationException"/>，不向上裸抛 <c>IOException</c>/<c>SecurityException</c>。
/// </summary>
/// <remarks>
/// 崩溃安全写入顺序（<c>CommitTarget</c>）冻结为：
/// <list type="number">
/// <item>写 <c>LastSourceRoot</c></item>
/// <item>写 <c>DataRoot</c> ← 逻辑提交点</item>
/// <item>删 <c>MigrationId</c></item>
/// <item>删 <c>PendingRoot</c></item>
/// </list>
/// 任意单值写入是 <c>RegSetValueEx</c> 级别的，不追求跨值原子；通过上面的顺序 + 恢复语义保证幂等可恢复。
/// </remarks>
internal sealed class RegistryStorageMigrationStateStore : IStorageMigrationStateStore, IStorageMigrationQueueStore
{
    private readonly string _keySubPath;
    private readonly string _defaultActiveRoot;

    /// <summary>
    /// 生产构造：键路径使用 <see cref="StorageRegistryKeys.KeyPath"/>，
    /// <paramref name="defaultActiveRoot"/> 为 DataRoot 缺失时的回退值。
    /// </summary>
    public RegistryStorageMigrationStateStore(string keySubPath, string defaultActiveRoot)
    {
        _keySubPath = keySubPath;
        _defaultActiveRoot = Path.GetFullPath(defaultActiveRoot);
    }

    /// <summary>生产工厂：使用正式 key 路径与默认 %LOCALAPPDATA%\Clipora。</summary>
    public static RegistryStorageMigrationStateStore CreateProduction()
    {
        return new RegistryStorageMigrationStateStore(
            StorageRegistryKeys.KeyPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipora"));
    }

    // ── IStorageMigrationStateStore ──────────────────────────────────

    /// <inheritdoc />
    public StorageMigrationState Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keySubPath, writable: false);

            string? dataRoot = ReadStringValue(key, StorageRegistryKeys.ValueNameDataRoot);
            string? pendingRoot = ReadStringValue(key, StorageRegistryKeys.ValueNamePendingRoot);
            string? migrationIdRaw = ReadStringValue(key, StorageRegistryKeys.ValueNameMigrationId);
            string? lastSourceRoot = ReadStringValue(key, StorageRegistryKeys.ValueNameLastSourceRoot);

            // ActiveRoot = 规范化 DataRoot（存在且非空），否则规范化 defaultActiveRoot
            string activeRoot = !string.IsNullOrWhiteSpace(dataRoot)
                ? Path.GetFullPath(dataRoot)
                : _defaultActiveRoot;

            // PendingRoot / LastSourceRoot：空白串也视为 null
            string? normalizedPending = !string.IsNullOrWhiteSpace(pendingRoot)
                ? Path.GetFullPath(pendingRoot)
                : null;

            Guid? migrationId = null;
            if (!string.IsNullOrWhiteSpace(migrationIdRaw)
                && Guid.TryParse(migrationIdRaw, out Guid parsed))
            {
                migrationId = parsed;
            }

            string? normalizedLastSource = !string.IsNullOrWhiteSpace(lastSourceRoot)
                ? Path.GetFullPath(lastSourceRoot)
                : null;

            return new StorageMigrationState(activeRoot, normalizedPending, migrationId, normalizedLastSource);
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (SecurityException ex)
        {
            throw new StorageLocationException(
                StorageLocationError.AccessDenied,
                $"无法访问注册表 HKCU\\{_keySubPath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new StorageLocationException(
                StorageLocationError.AccessDenied,
                $"无法访问注册表 HKCU\\{_keySubPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"注册表读取失败 HKCU\\{_keySubPath}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not StorageLocationException)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"注册表配置异常 HKCU\\{_keySubPath}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void ClearPending(Guid migrationId)
    {
        try
        {
            StorageMigrationState state = Read();
            if (state.MigrationId != migrationId)
                throw new StorageLocationException(
                    StorageLocationError.InvalidPath,
                    $"MigrationId mismatch: expected {state.MigrationId}, got {migrationId}");

            using var key = Registry.CurrentUser.OpenSubKey(_keySubPath, writable: true);
            if (key is not null)
            {
                key.DeleteValue(StorageRegistryKeys.ValueNamePendingRoot, throwOnMissingValue: false);
                key.DeleteValue(StorageRegistryKeys.ValueNameMigrationId, throwOnMissingValue: false);
            }
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapException(ex);
        }
    }

    /// <inheritdoc />
    public void CommitTarget(string targetRoot, string sourceRoot, Guid migrationId)
    {
        try
        {
            StorageMigrationState state = Read();
            if (state.MigrationId != migrationId)
                throw new StorageLocationException(
                    StorageLocationError.InvalidPath,
                    $"MigrationId mismatch: expected {state.MigrationId}, got {migrationId}");

            string normalizedTarget = Path.GetFullPath(targetRoot);
            string normalizedSource = Path.GetFullPath(sourceRoot);

            using var key = Registry.CurrentUser.CreateSubKey(_keySubPath);

            // 冻结写入顺序（崩溃安全，对齐 057 D4 与 060 §9）：
            // 1. 写 LastSourceRoot = source（同值幂等，无副作用）
            key.SetValue(StorageRegistryKeys.ValueNameLastSourceRoot, normalizedSource, RegistryValueKind.String);

            // 2. 写 DataRoot = target ← 逻辑提交点
            key.SetValue(StorageRegistryKeys.ValueNameDataRoot, normalizedTarget, RegistryValueKind.String);

            // 3. 删 MigrationId
            key.DeleteValue(StorageRegistryKeys.ValueNameMigrationId, throwOnMissingValue: false);

            // 4. 删 PendingRoot
            key.DeleteValue(StorageRegistryKeys.ValueNamePendingRoot, throwOnMissingValue: false);
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapException(ex);
        }
    }

    // ── 3a-2/3a-3 入队与诊断原语 ────────────────────────────────────

    /// <summary>
    /// 入队一次迁移：仅写 <c>PendingRoot</c> + <c>MigrationId</c>，不动 <c>DataRoot</c>/<c>LastSourceRoot</c>。
    /// 崩溃后由恢复逻辑兜底（见 §4 不变量）。
    /// </summary>
    public void Enqueue(string targetRoot, Guid migrationId)
    {
        try
        {
            string normalizedTarget = Path.GetFullPath(targetRoot);

            using var key = Registry.CurrentUser.CreateSubKey(_keySubPath);

            // 先 MigrationId 后 PendingRoot（单次 SetValue 两调用；崩溃中断由恢复逻辑兜底）
            key.SetValue(StorageRegistryKeys.ValueNameMigrationId, migrationId.ToString("D"), RegistryValueKind.String);
            key.SetValue(StorageRegistryKeys.ValueNamePendingRoot, normalizedTarget, RegistryValueKind.String);
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapException(ex);
        }
    }

    // ── 内部帮助方法 ────────────────────────────────────────────────

    /// <summary>读取一个 REG_SZ 值；值不存在返回 null，类型非 REG_SZ 抛受控异常。</summary>
    private string? ReadStringValue(RegistryKey? key, string valueName)
    {
        if (key is null)
            return null;

        var names = key.GetValueNames();
        if (!Array.Exists(names, n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var kind = key.GetValueKind(valueName);
        if (kind != RegistryValueKind.String)
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"注册表值 HKCU\\{_keySubPath}\\{valueName} 类型错误（期望 REG_SZ，实际 {kind}）");

        return key.GetValue(valueName) as string;
    }

    /// <summary>将裸系统异常映射为受控 <see cref="StorageLocationException"/>。</summary>
    private StorageLocationException MapException(Exception ex)
    {
        return ex switch
        {
            SecurityException
                => new StorageLocationException(
                    StorageLocationError.AccessDenied,
                    $"无法访问注册表 HKCU\\{_keySubPath}: {ex.Message}"),

            UnauthorizedAccessException
                => new StorageLocationException(
                    StorageLocationError.AccessDenied,
                    $"无法访问注册表 HKCU\\{_keySubPath}: {ex.Message}"),

            IOException
                => new StorageLocationException(
                    StorageLocationError.InvalidPath,
                    $"注册表写入失败 HKCU\\{_keySubPath}: {ex.Message}"),

            _ => new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"注册表配置异常 HKCU\\{_keySubPath}: {ex.Message}"),
        };
    }
}
