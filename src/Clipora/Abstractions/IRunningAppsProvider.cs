using System.Collections.Generic;

namespace Clipora.Abstractions;

/// <summary>供"添加应用"排除选择器枚举当前有可见主窗口的用户应用。</summary>
public readonly record struct RunningAppInfo(string ProcessName, string DisplayName);

public interface IRunningAppsProvider
{
    /// <summary>枚举当前有可见主窗口的用户进程（按归一化进程名去重、排除自身），按 DisplayName 排序。</summary>
    IReadOnlyList<RunningAppInfo> GetUserApps();
}
