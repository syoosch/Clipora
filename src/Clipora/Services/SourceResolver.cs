using System;
using System.Diagnostics;
using Clipora.Abstractions;
using Clipora.Interop;

namespace Clipora.Services;

/// <summary><see cref="ISourceResolver"/> 实现：由窗口句柄解析所属进程。</summary>
public sealed class SourceResolver : ISourceResolver
{
    public string? GetClipboardOwnerAppName()
    {
        string? ownerName = GetAppNameFromWindow(NativeMethods.GetClipboardOwner());
        return ownerName ?? GetForegroundAppName();
    }

    public string? GetForegroundAppName()
        => GetAppNameFromWindow(NativeMethods.GetForegroundWindow());

    public string? GetForegroundProcessName()
        => GetProcessNameFromWindow(NativeMethods.GetForegroundWindow());

    private static string? GetAppNameFromWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return null;

            using var process = Process.GetProcessById((int)pid);

            try
            {
                string? description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                    return description;
            }
            catch
            {
                // 受限/系统进程无法读取模块信息时回退到进程名。
            }

            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessNameFromWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return null;

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
