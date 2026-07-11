namespace Clipora.Services;

/// <summary>
/// 注册表路径与值名常量。生产 <see cref="RegistryStorageRootLocator"/> 与
/// <see cref="RegistryStorageMigrationStateStore"/> 共享同一份键路径真相，避免形成第二份真相源。
/// 仍只存路径与迁移控制状态，绝不存剪贴板内容（延续 <c>057 D1</c>）。
/// </summary>
internal static class StorageRegistryKeys
{
    /// <summary>HKCU 子键路径。</summary>
    public const string KeyPath = @"Software\Clipora\Storage";

    /// <summary>当前激活 Root（REG_SZ）。缺失 = 使用默认 %LOCALAPPDATA%\Clipora。</summary>
    public const string ValueNameDataRoot = "DataRoot";

    /// <summary>迁移目标（已入队/进行中）。完成或失败清理后删除。</summary>
    public const string ValueNamePendingRoot = "PendingRoot";

    /// <summary>迁移 GUID（"D" 格式）。与 PendingRoot 同生命周期。</summary>
    public const string ValueNameMigrationId = "MigrationId";

    /// <summary>最近一次成功迁移的 source（信息性，首版不据此删旧 Root）。</summary>
    public const string ValueNameLastSourceRoot = "LastSourceRoot";
}
