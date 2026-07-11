namespace Clipora.Abstractions;

/// <summary>管理 Windows 注册表自启动项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。</summary>
public interface IAutoStartService
{
    /// <summary>当前是否已注册开机自启。</summary>
    bool IsEnabled();

    /// <summary>写入或移除注册表自启项。失败时 <paramref name="error"/> 返回人话提示。</summary>
    bool TrySetEnabled(bool enabled, out string? error);
}
