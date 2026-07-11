namespace Clipora.Models;

/// <summary>Root 解析来源，按优先级从高到低排列。</summary>
public enum StorageRootSource
{
    /// <summary>构造函数显式 override（自检等）。</summary>
    Override,

    /// <summary>环境变量 CLIPORA_DATA_DIR。</summary>
    Environment,

    /// <summary>HKCU\Software\Clipora\Storage 的 DataRoot。</summary>
    Locator,

    /// <summary>%LOCALAPPDATA%\Clipora。</summary>
    Default,
}

/// <summary>一次 Root 解析的完整结果。</summary>
public sealed record StorageRootResolution(
    string Root,
    StorageRootSource Source,
    bool CanMigrate);

/// <summary>locator 故障的受控错误分类。</summary>
public enum StorageLocationError
{
    /// <summary>路径非法（相对、含无效字符、无法解析）。</summary>
    InvalidPath,

    /// <summary>路径为绝对本地路径，但目录不存在。</summary>
    MissingDirectory,

    /// <summary>无权访问目标路径。</summary>
    AccessDenied,

    /// <summary>不支持的网络路径（UNC）。</summary>
    UnsupportedNetworkPath,
}

/// <summary>
/// locator 解析失败时的受控异常。始终携带 <see cref="StorageLocationError"/> 错误码；
/// 上层不得向 UI 透出未处理的 ArgumentException / IOException。
/// </summary>
public sealed class StorageLocationException : Exception
{
    public StorageLocationError ErrorCode { get; }

    /// <summary>与错误关联的规范化路径；UI/恢复逻辑不得从 Message 反解析路径。</summary>
    public string? PathValue { get; }

    public StorageLocationException(StorageLocationError errorCode, string message, string? pathValue = null)
        : base(message)
    {
        ErrorCode = errorCode;
        PathValue = pathValue;
    }
}
