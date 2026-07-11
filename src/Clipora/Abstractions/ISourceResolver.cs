namespace Clipora.Abstractions;

/// <summary>解析剪贴板来源与当前前台窗口所属应用。</summary>
public interface ISourceResolver
{
    /// <summary>返回当前剪贴板 owner 的显示名/进程名；无法解析时回退到前台应用；仍失败返回 null。</summary>
    string? GetClipboardOwnerAppName();

    /// <summary>返回前台应用的显示名/进程名；无法解析时返回 null。</summary>
    string? GetForegroundAppName();

    /// <summary>返回前台进程的归一化匹配键（lowercase、无扩展名）；失败返回 null。供隐私应用排除匹配。</summary>
    string? GetForegroundProcessName();
}
