using System.IO;
using System.Security;
using Clipora.Models;
using Microsoft.Win32;

namespace Clipora.Services;

/// <summary>内部 locator 抽象，供 <see cref="StorageLocationService"/> 和自检注入。</summary>
internal interface IStorageRootLocator
{
    /// <summary>
    /// 返回 HKCU locator 中的 DataRoot。
    /// 值不存在时返回 null；值存在但类型非 REG_SZ 或为空、或任何注册表访问失败时抛 <see cref="StorageLocationException"/>。
    /// </summary>
    string? GetDataRoot();
}

/// <summary>生产实现：读取 HKCU\Software\Clipora\Storage\DataRoot（REG_SZ）。</summary>
internal sealed class RegistryStorageRootLocator : IStorageRootLocator
{
    private const string KeyPath = StorageRegistryKeys.KeyPath;
    private const string ValueName = StorageRegistryKeys.ValueNameDataRoot;

    public string? GetDataRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key is null)
                return null;

            // 值不存在 → 未配置
            var names = key.GetValueNames();
            if (!Array.Exists(names, n => string.Equals(n, ValueName, StringComparison.OrdinalIgnoreCase)))
                return null;

            // 值存在 → 校验类型必须为 REG_SZ
            var kind = key.GetValueKind(ValueName);
            if (kind != RegistryValueKind.String)
                throw new StorageLocationException(
                    StorageLocationError.InvalidPath,
                    $"注册表值 HKCU\\{KeyPath}\\{ValueName} 类型错误（期望 REG_SZ，实际 {kind}）");

            var value = key.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                throw new StorageLocationException(
                    StorageLocationError.InvalidPath,
                    $"注册表值 HKCU\\{KeyPath}\\{ValueName} 不可为空");

            return value;
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (SecurityException ex)
        {
            throw new StorageLocationException(
                StorageLocationError.AccessDenied,
                $"无法访问注册表 HKCU\\{KeyPath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new StorageLocationException(
                StorageLocationError.AccessDenied,
                $"无法访问注册表 HKCU\\{KeyPath}: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"注册表读取失败 HKCU\\{KeyPath}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not StorageLocationException)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"注册表配置异常 HKCU\\{KeyPath}: {ex.Message}");
        }
    }
}

/// <summary>自检用内存桩：可配置的返回路径。</summary>
internal sealed class MemoryStorageRootLocator : IStorageRootLocator
{
    private readonly string? _root;

    public MemoryStorageRootLocator(string? root) => _root = root;

    public string? GetDataRoot() => _root;

    /// <summary>返回一个永远返回 null 的实例（模拟无 locator 状态）。</summary>
    public static MemoryStorageRootLocator None { get; } = new(null);
}
