using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Clipora.Services;

/// <summary>显式默认位置恢复错误码（3a-3c-1）。</summary>
internal enum StorageDefaultRecoveryError
{
    None,
    InvalidExpectedRoot,
    StateChanged,
    PendingExists,
    AlreadyDefault,
    AccessDenied,
    Unknown,
}

/// <summary>显式默认位置恢复结果（3a-3c-1）。</summary>
internal sealed record StorageDefaultRecoveryResult(
    bool Succeeded,
    string DefaultRoot,
    StorageDefaultRecoveryError Error,
    string? Detail);

/// <summary>显式默认位置恢复服务抽象（3a-3c-1）。仅在用户明确选择"使用默认位置"后调用。</summary>
internal interface IStorageDefaultRecoveryService
{
    StorageDefaultRecoveryResult RecoverMissingRoot(string expectedUnavailableRoot);
}

/// <summary>
/// 最小注册表键操作抽象：供自检注入模拟删除后复验失败等场景。
/// 不得用于修改 state store / locator 行为。
/// </summary>
internal interface IRecoveryRegistryKey : IDisposable
{
    string? GetStringValue(string valueName);
    RegistryValueKind? GetValueKind(string valueName);
    string[] GetValueNames();
    void SetStringValue(string valueName, string value);
    void DeleteValue(string valueName);
    bool ValueExists(string valueName);
}

/// <summary>
/// 生产注册表键适配器。
/// </summary>
internal sealed class ProductionRecoveryRegistryKey : IRecoveryRegistryKey
{
    private readonly RegistryKey _key;

    public ProductionRecoveryRegistryKey(RegistryKey key) => _key = key;

    public string? GetStringValue(string valueName) =>
        _key.GetValue(valueName) as string;

    public RegistryValueKind? GetValueKind(string valueName)
        => _key.GetValueKind(valueName);

    public string[] GetValueNames() => _key.GetValueNames();

    public void SetStringValue(string valueName, string value) =>
        _key.SetValue(valueName, value, RegistryValueKind.String);

    public void DeleteValue(string valueName) =>
        _key.DeleteValue(valueName, throwOnMissingValue: false);

    public bool ValueExists(string valueName)
    {
        foreach (string name in _key.GetValueNames())
            if (string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// 生产实现：使用 <c>HKCU\Software\Clipora\Storage</c>。
/// 提供 internal 隔离键构造供自检。
/// </summary>
internal sealed class StorageDefaultRecoveryService : IStorageDefaultRecoveryService
{
    private readonly string _keySubPath;
    private readonly string _defaultRoot;
    private readonly Func<bool, IRecoveryRegistryKey?> _openKey;

    /// <summary>生产构造：使用 <c>HKCU\Software\Clipora\Storage</c>。</summary>
    public StorageDefaultRecoveryService()
    {
        _keySubPath = StorageRegistryKeys.KeyPath;
        _defaultRoot = Path.GetFullPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipora"));
        _openKey = writable =>
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keySubPath, writable);
            return key is not null ? new ProductionRecoveryRegistryKey(key) : null;
        };
    }

    /// <summary>隔离键构造（自检用）：使用指定子键路径，不会触碰生产键。</summary>
    internal StorageDefaultRecoveryService(string keySubPath, string defaultRoot)
    {
        _keySubPath = keySubPath;
        _defaultRoot = Path.GetFullPath(defaultRoot);
        _openKey = writable =>
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keySubPath, writable);
            return key is not null ? new ProductionRecoveryRegistryKey(key) : null;
        };
    }

    /// <summary>注入 registry seam 的构造（自检模拟复验失败 / 异常映射等）。</summary>
    internal StorageDefaultRecoveryService(
        string keySubPath,
        string defaultRoot,
        Func<bool, IRecoveryRegistryKey?> openKey)
    {
        _keySubPath = keySubPath;
        _defaultRoot = Path.GetFullPath(defaultRoot);
        _openKey = openKey;
    }

    /// <inheritdoc />
    public StorageDefaultRecoveryResult RecoverMissingRoot(string expectedUnavailableRoot)
    {
        try
        {
            // 1. expected 必须完全限定并规范化
            if (string.IsNullOrWhiteSpace(expectedUnavailableRoot)
                || !Path.IsPathFullyQualified(expectedUnavailableRoot))
            {
                return Fail(StorageDefaultRecoveryError.InvalidExpectedRoot, "需要完全限定的本地路径。");
            }

            string expected;
            try { expected = Path.GetFullPath(expectedUnavailableRoot); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Fail(StorageDefaultRecoveryError.InvalidExpectedRoot, $"路径无效: {ex.Message}");
            }

            // 2. expected 与默认 Root 相同 → 拒绝
            if (string.Equals(expected, _defaultRoot, StringComparison.OrdinalIgnoreCase))
                return Fail(StorageDefaultRecoveryError.AlreadyDefault, "当前数据目录已经是默认位置，无需恢复。");

            // 3. 打开 writable key 并校验 DataRoot
            using IRecoveryRegistryKey? key = _openKey(true);
            if (key is null)
                return Fail(StorageDefaultRecoveryError.StateChanged, "注册表键不存在，无法确认当前数据目录。");

            // 检查 DataRoot 值
            bool hasDataRoot = key.ValueExists(StorageRegistryKeys.ValueNameDataRoot);
            if (!hasDataRoot)
                return Fail(StorageDefaultRecoveryError.StateChanged, "注册表 DataRoot 不存在。");

            // DataRoot 必须是 REG_SZ
            RegistryValueKind? dataRootKind = key.GetValueKind(StorageRegistryKeys.ValueNameDataRoot);
            if (dataRootKind != RegistryValueKind.String)
                return Fail(StorageDefaultRecoveryError.StateChanged,
                    $"DataRoot 类型错误（期望 REG_SZ，实际 {dataRootKind}）。");

            string? currentDataRoot = key.GetStringValue(StorageRegistryKeys.ValueNameDataRoot);
            if (string.IsNullOrWhiteSpace(currentDataRoot))
                return Fail(StorageDefaultRecoveryError.StateChanged, "DataRoot 为空。");

            string normalizedCurrent;
            try { normalizedCurrent = Path.GetFullPath(currentDataRoot); }
            catch
            {
                return Fail(StorageDefaultRecoveryError.StateChanged, "DataRoot 路径无效。");
            }

            if (!string.Equals(normalizedCurrent, expected, StringComparison.OrdinalIgnoreCase))
                return Fail(StorageDefaultRecoveryError.StateChanged,
                    "DataRoot 与期望路径不一致，可能已被其他程序修改。");

            // 4. 检查 pending：PendingRoot 或 MigrationId 任一存在 → 拒绝
            if (HasAnyPendingValue(key))
                return Fail(StorageDefaultRecoveryError.PendingExists,
                    "已有存储迁移等待处理，请先完成或取消迁移后再恢复默认位置。");

            // 5. 只删除 DataRoot 与 LastSourceRoot
            key.DeleteValue(StorageRegistryKeys.ValueNameDataRoot);
            key.DeleteValue(StorageRegistryKeys.ValueNameLastSourceRoot);

            // 6. 复验删除
            if (key.ValueExists(StorageRegistryKeys.ValueNameDataRoot))
                return Fail(StorageDefaultRecoveryError.Unknown, "删除 DataRoot 后复验失败，注册表值仍存在。");

            if (key.ValueExists(StorageRegistryKeys.ValueNameLastSourceRoot))
                return Fail(StorageDefaultRecoveryError.Unknown, "删除 LastSourceRoot 后复验失败，注册表值仍存在。");

            // 7. 不创建默认目录 — 由 3c-2 的组合根负责

            return new StorageDefaultRecoveryResult(true, _defaultRoot, StorageDefaultRecoveryError.None, null);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return Fail(StorageDefaultRecoveryError.AccessDenied, $"注册表访问被拒绝: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail(StorageDefaultRecoveryError.Unknown, $"恢复默认位置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否存在任何 pending 痕迹：PendingRoot 或 MigrationId 值存在（含空白/畸形/错误类型）。
    /// </summary>
    private static bool HasAnyPendingValue(IRecoveryRegistryKey key)
    {
        foreach (string valueName in key.GetValueNames())
        {
            if (string.Equals(valueName, StorageRegistryKeys.ValueNamePendingRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(valueName, StorageRegistryKeys.ValueNameMigrationId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private StorageDefaultRecoveryResult Fail(StorageDefaultRecoveryError error, string detail) =>
        new(false, _defaultRoot, error, detail);
}
