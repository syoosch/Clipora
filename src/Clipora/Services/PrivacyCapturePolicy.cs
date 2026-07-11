using System;
using System.Collections.Generic;

namespace Clipora.Services;

/// <summary>
/// 隐私捕获判定纯函数。把"是否捕获"从 <see cref="ClipboardMonitorService.OnClipboardUpdate"/> 抽成无副作用 seam，供 selftest。
/// </summary>
public static class PrivacyCapturePolicy
{
    /// <summary>
    /// 返回 true 表示应继续捕获；false 表示跳过（暂停 / 系统排除 / 前台应用在排除名单中）。
    /// </summary>
    /// <param name="paused">是否已暂停记录</param>
    /// <param name="foregroundProcessName">已归一化的前台进程名（lowercase），或 null 表示解析失败</param>
    /// <param name="excludedApps">已归一化的应用排除名单（lowercase 进程名）</param>
    /// <param name="systemExcluded">系统排除标记是否命中</param>
    public static bool ShouldCapture(
        bool paused,
        string? foregroundProcessName,
        IReadOnlyCollection<string> excludedApps,
        bool systemExcluded)
    {
        // 暂停 → 跳过
        if (paused)
            return false;

        // 系统排除标记 → 跳过
        if (systemExcluded)
            return false;

        // 前台进程命中排除名单 → 跳过（OrdinalIgnoreCase 全等，不做子串匹配）
        if (foregroundProcessName is not null && excludedApps.Count > 0)
        {
            foreach (string excluded in excludedApps)
            {
                if (string.Equals(foregroundProcessName, excluded, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        // 其余（含 process 为 null，即解析失败）→ 捕获（fail-open）
        return true;
    }
}
