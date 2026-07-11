using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>
/// 解析当前数据 Root（优先级：override > CLIPORA_DATA_DIR > HKCU locator > 默认）。
/// 安装版可迁移（Locator / Default）；override / 环境变量锁定不可迁移。
/// </summary>
public interface IStorageLocationService
{
    /// <summary>
    /// 按优先级惰性解析 Root。override / env 命中后不访问注册表。
    /// locator 非法/丢失/无权限时抛出 <see cref="StorageLocationException"/>（fail-closed）。
    /// </summary>
    StorageRootResolution Resolve(string? overrideDir = null);
}
