using System;
using Clipora.Abstractions;
using Microsoft.Win32;

namespace Clipora.Services;

/// <summary>通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 管理开机自启。
/// 不写 HKLM、不请求管理员、不联网。</summary>
public sealed class AutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public AutoStartService(string valueName = "Clipora")
    {
        _valueName = valueName;
    }

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? existing = key?.GetValue(_valueName) as string;
            return string.Equals(existing, GetExePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开注册表启动项";
                return false;
            }

            if (enabled)
                key.SetValue(_valueName, GetExePath());
            else
                key.DeleteValue(_valueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = enabled ? "无法写入启动项：权限不足" : "无法移除启动项：权限不足";
            return false;
        }
        catch (Exception)
        {
            error = enabled ? "无法写入启动项" : "无法移除启动项";
            return false;
        }
    }

    private static string GetExePath() =>
        $"\"{Environment.ProcessPath}\"";
}
