using System.Collections.Generic;

namespace Clipora.Services;

public enum PrivacyCaptureDecision
{
    Capture,
    Skip,
    Retry,
}

/// <summary>被动捕获隐私状态机；Unknown 只允许一次非阻塞重试，重试后 fail-closed。</summary>
public static class PrivacyCapturePolicy
{
    public static PrivacyCaptureDecision Decide(
        bool paused,
        string? foregroundProcessName,
        IReadOnlyCollection<string> excludedApps,
        ClipboardExclusionState systemExclusion,
        bool isRetry)
    {
        if (paused)
            return PrivacyCaptureDecision.Skip;

        if (systemExclusion == ClipboardExclusionState.Excluded)
            return PrivacyCaptureDecision.Skip;
        if (systemExclusion == ClipboardExclusionState.Unknown)
            return isRetry ? PrivacyCaptureDecision.Skip : PrivacyCaptureDecision.Retry;

        // 没有应用排除名单时，前台进程名只用于排除匹配，解析失败不应误伤正常捕获。
        if (excludedApps.Count == 0)
            return PrivacyCaptureDecision.Capture;

        if (foregroundProcessName is null)
            return isRetry ? PrivacyCaptureDecision.Skip : PrivacyCaptureDecision.Retry;

        foreach (string excluded in excludedApps)
        {
            if (string.Equals(foregroundProcessName, excluded, StringComparison.OrdinalIgnoreCase))
                return PrivacyCaptureDecision.Skip;
        }
        return PrivacyCaptureDecision.Capture;
    }

    /// <summary>旧 bool seam 保留给既有调用/测试；不表示 Unknown，不执行重试。</summary>
    public static bool ShouldCapture(
        bool paused,
        string? foregroundProcessName,
        IReadOnlyCollection<string> excludedApps,
        bool systemExcluded) =>
        Decide(
            paused,
            foregroundProcessName,
            excludedApps,
            systemExcluded ? ClipboardExclusionState.Excluded : ClipboardExclusionState.Allowed,
            isRetry: true) == PrivacyCaptureDecision.Capture;
}

/// <summary>剪贴板序列号门闩：新事件/Stop/Dispose 可取消，旧事件不得在序列变化后继续。</summary>
internal sealed class ClipboardPrivacyRetryState
{
    internal uint? PendingSequence { get; private set; }

    internal void Schedule(uint sequence) => PendingSequence = sequence;

    internal bool TryConsume(uint currentSequence)
    {
        uint? pending = PendingSequence;
        PendingSequence = null;
        return pending.HasValue && pending.Value == currentSequence;
    }

    internal void Cancel() => PendingSequence = null;
}
