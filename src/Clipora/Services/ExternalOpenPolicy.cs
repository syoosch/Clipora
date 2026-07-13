using System.Diagnostics;
using System.IO;

namespace Clipora.Services;

internal enum ExternalOpenDecision
{
    Reject,
    Allow,
    RequireConfirmation,
}

internal readonly record struct ExternalOpenRequest(string TargetPath, string FileName, string FailureMessage);

internal static class ExternalOpenPolicy
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".cpl", ".msi", ".msp", ".bat", ".cmd",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".lnk", ".url", ".reg", ".application", ".appref-ms",
    };

    internal static ExternalOpenDecision EvaluateUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return ExternalOpenDecision.Reject;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
            ? ExternalOpenDecision.Allow
            : ExternalOpenDecision.Reject;
    }

    internal static ExternalOpenDecision EvaluateFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ExternalOpenDecision.Reject;
        try
        {
            string extension = Path.GetExtension(path);
            return DangerousExtensions.Contains(extension)
                ? ExternalOpenDecision.RequireConfirmation
                : ExternalOpenDecision.Allow;
        }
        catch
        {
            return ExternalOpenDecision.Reject;
        }
    }
}

internal interface IExternalLauncher
{
    void Launch(string target);
}

internal sealed class ShellExternalLauncher : IExternalLauncher
{
    public void Launch(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }
}

/// <summary>把纯策略与可替换 launcher 组合，确保危险文件只有确认入口能启动。</summary>
internal sealed class ExternalOpenCoordinator(IExternalLauncher launcher)
{
    private readonly IExternalLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));

    internal ExternalOpenRequest? OpenFile(string path, string failureMessage)
    {
        return ExternalOpenPolicy.EvaluateFile(path) switch
        {
            ExternalOpenDecision.Allow => LaunchAndReturnNull(path),
            ExternalOpenDecision.RequireConfirmation => new ExternalOpenRequest(
                path,
                Path.GetFileName(path),
                failureMessage),
            _ => throw new InvalidOperationException("文件路径无法安全打开"),
        };
    }

    internal void OpenUrl(string url)
    {
        if (ExternalOpenPolicy.EvaluateUrl(url) != ExternalOpenDecision.Allow)
            throw new InvalidOperationException("仅支持打开 HTTP/HTTPS 链接");
        _launcher.Launch(url);
    }

    internal void Confirm(ExternalOpenRequest request)
    {
        // 确认时重新验证，避免请求产生后路径/扩展名被替换。
        if (ExternalOpenPolicy.EvaluateFile(request.TargetPath) != ExternalOpenDecision.RequireConfirmation)
            throw new InvalidOperationException("文件类型已变化，已取消打开");
        _launcher.Launch(request.TargetPath);
    }

    private ExternalOpenRequest? LaunchAndReturnNull(string target)
    {
        _launcher.Launch(target);
        return null;
    }
}
