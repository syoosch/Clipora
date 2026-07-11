using System.IO;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary><see cref="IStorageLocationService"/> 的实现：优先级解析 + locator fail-closed。</summary>
public sealed class StorageLocationService : IStorageLocationService
{
    private readonly IStorageRootLocator _locator;

    internal StorageLocationService(IStorageRootLocator locator)
    {
        _locator = locator;
    }

    /// <summary>生产构造：使用 HKCU 注册表 locator。</summary>
    public static StorageLocationService CreateProduction() => new(new RegistryStorageRootLocator());

    /// <inheritdoc/>
    public StorageRootResolution Resolve(string? overrideDir = null)
    {
        try
        {
            // 1. 显式构造参数（自检等）— 不可迁移
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                if (!Path.IsPathFullyQualified(overrideDir!))
                    throw new StorageLocationException(
                        StorageLocationError.InvalidPath,
                        $"构造参数必须是完全限定路径: {overrideDir}");
                return new StorageRootResolution(
                    NormalizePath(overrideDir!, "构造参数"),
                    StorageRootSource.Override,
                    CanMigrate: false);
            }

            // 2. 环境变量 CLIPORA_DATA_DIR — 不可迁移
            string? env = Environment.GetEnvironmentVariable("CLIPORA_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (!Path.IsPathFullyQualified(env))
                    throw new StorageLocationException(
                        StorageLocationError.InvalidPath,
                        $"环境变量 CLIPORA_DATA_DIR 必须是完全限定路径，当前值: {env}");
                return new StorageRootResolution(
                    NormalizePath(env, "环境变量 CLIPORA_DATA_DIR"),
                    StorageRootSource.Environment,
                    CanMigrate: false);
            }

            // 3. HKCU locator DataRoot — 可迁移，fail-closed
            string? locatorRoot = _locator.GetDataRoot();
            if (!string.IsNullOrWhiteSpace(locatorRoot))
            {
                ValidateLocatorRoot(locatorRoot!);
                return new StorageRootResolution(
                    NormalizePath(locatorRoot!, "locator"),
                    StorageRootSource.Locator,
                    CanMigrate: true);
            }

            // 4. 默认 %LOCALAPPDATA%\Clipora — 可迁移
            string defaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipora");
            return new StorageRootResolution(defaultRoot, StorageRootSource.Default, CanMigrate: true);
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"路径解析失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Path.GetFullPath 的受控包装：将 ArgumentException / NotSupportedException / PathTooLongException
    /// 等统一转换为 StorageLocationException(InvalidPath)，不透出裸异常到 UI。
    /// </summary>
    private static string NormalizePath(string path, string context)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"{context}: 路径无效 - {path} ({ex.Message})");
        }
    }

    /// <summary>
    /// 校验 locator 返回的路径：必须完全限定、非 UNC/网络映射盘、目录存在、可访问。
    /// 任何不满足条件均抛 <see cref="StorageLocationException"/>（fail-closed）。
    /// </summary>
    internal static void ValidateLocatorRoot(string root)
    {
        // 必须是完全限定路径（拒绝 C:relative 等）
        if (!Path.IsPathFullyQualified(root))
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"配置的数据目录必须是完全限定路径: {root}");

        try
        {
            string full = NormalizePath(root, "locator");

            // 拒绝 UNC / 网络路径（包括 \\host\share 和 \\?\UNC\），但允许本地扩展路径 \\?\C:\...
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                bool isExtendedLocal = full.StartsWith(@"\\?\", StringComparison.Ordinal)
                    && !full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
                if (!isExtendedLocal)
                    throw new StorageLocationException(
                        StorageLocationError.UnsupportedNetworkPath,
                        $"不支持网络路径作为数据目录: {full}",
                        full);
            }

            // 拒绝网络映射驱动器（如 Z:\ 映射到 \\server\share）
            string? rootPart = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(rootPart))
            {
                try
                {
                    var driveInfo = new DriveInfo(rootPart);
                    if (driveInfo.DriveType == DriveType.Network)
                        throw new StorageLocationException(
                            StorageLocationError.UnsupportedNetworkPath,
                            $"不支持网络驱动器作为数据目录: {full}（{rootPart} 为网络映射驱动器）",
                            full);
                }
                catch (ArgumentException)
                {
                    // 驱动器号无效（极少见），继续其他校验
                }
            }

            // 使用 GetAttributes 区分“确实不存在”和“存在但无权访问”。Directory.Exists 会把两者都压成 false，
            // 从而错误开放“使用默认位置”操作。
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(full);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            {
                throw new StorageLocationException(
                    StorageLocationError.MissingDirectory,
                    $"配置的数据目录不可用: {full}",
                    full);
            }

            if ((attributes & FileAttributes.Directory) == 0)
                throw new StorageLocationException(
                    StorageLocationError.InvalidPath,
                    $"配置的数据目录不是文件夹: {full}",
                    full);

            // 最小只读访问探针；不创建/修改任何文件。
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (StorageLocationException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new StorageLocationException(
                StorageLocationError.AccessDenied,
                $"无法访问配置的数据目录: {root}",
                root);
        }
        catch (Exception ex) when (ex is not StorageLocationException)
        {
            throw new StorageLocationException(
                StorageLocationError.InvalidPath,
                $"配置的数据目录无效: {root} ({ex.Message})");
        }
    }
}
