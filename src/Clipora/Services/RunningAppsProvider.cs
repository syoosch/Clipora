using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Clipora.Abstractions;

namespace Clipora.Services;

/// <summary><see cref="IRunningAppsProvider"/> 实现：枚举有可见主窗口的用户进程。</summary>
public sealed class RunningAppsProvider : IRunningAppsProvider
{
    public IReadOnlyList<RunningAppInfo> GetUserApps()
    {
        var results = new List<RunningAppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    FilterAndCollect(process, results, seen);
                }
                catch
                {
                    // 个别受保护/已退出进程，跳过。
                }

                process.Dispose();
            }
        }
        catch
        {
            // 整个枚举失败不可能发生，但 fail-safe。
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        return results;
    }

    /// <summary>核心纯函数逻辑：过滤无窗口/空标题、按归一化名去重、DisplayName 回退、排除自身。供自检。</summary>
    public static bool TryResolveAppInfo(
        int processId,
        string? mainWindowTitle,
        string? processName,
        string? fileDescription,
        HashSet<string> seen,
        out RunningAppInfo info)
    {
        info = default;

        // 过滤：无可见窗口或空标题
        if (string.IsNullOrEmpty(mainWindowTitle))
            return false;

        // 归一化 ProcessName
        string? normalized = processName?.ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
            return false;

        // 排除 Clipora 自身
        if (normalized == "clipora")
            return false;

        // 按归一化名去重
        if (!seen.Add(normalized))
            return false;

        // DisplayName = FileDescription，空则回退 ProcessName
        string displayName = !string.IsNullOrWhiteSpace(fileDescription)
            ? fileDescription
            : processName!;

        info = new RunningAppInfo(normalized, displayName);
        return true;
    }

    private static void FilterAndCollect(Process process, List<RunningAppInfo> results, HashSet<string> seen)
    {
        string? title = null;
        try { title = process.MainWindowTitle; } catch { }

        if (string.IsNullOrEmpty(title))
            return;

        string processName = process.ProcessName;
        string? description = null;
        try { description = process.MainModule?.FileVersionInfo.FileDescription; } catch { }

        if (TryResolveAppInfo(process.Id, title, processName, description, seen, out RunningAppInfo info))
            results.Add(info);
    }
}
