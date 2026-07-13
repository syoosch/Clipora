namespace Clipora.Services;

public enum ClipboardExclusionState
{
    Allowed,
    Excluded,
    Unknown,
}

/// <summary>
/// 系统排除标记纯函数判定。活体 <see cref="System.Windows.Clipboard"/> 读取放在 monitor（4.4.2b），selftest 不碰剪贴板。
/// </summary>
public static class ClipboardExclusionMarker
{
    public static ClipboardExclusionState Evaluate(
        bool hasExcludeFormat,
        bool hasHistoryFlag,
        int historyFlagValue,
        bool historyFlagValueKnown)
    {
        if (hasExcludeFormat)
            return ClipboardExclusionState.Excluded;
        if (!hasHistoryFlag)
            return ClipboardExclusionState.Allowed;
        if (!historyFlagValueKnown)
            return ClipboardExclusionState.Unknown;
        return historyFlagValue == 0
            ? ClipboardExclusionState.Excluded
            : ClipboardExclusionState.Allowed;
    }

    /// <summary>
    /// 返回 true 表示应排除（系统已标记此剪贴板内容不应进入历史）。
    /// </summary>
    /// <param name="hasExcludeFormat"><c>Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")</c></param>
    /// <param name="hasHistoryFlag"><c>Clipboard.ContainsData("CanIncludeInClipboardHistory")</c></param>
    /// <param name="historyFlagValue"><c>CanIncludeInClipboardHistory</c> 的 DWORD 值（仅 hasHistoryFlag=true 时有效）</param>
    public static bool IsExcluded(bool hasExcludeFormat, bool hasHistoryFlag, int historyFlagValue)
        => Evaluate(hasExcludeFormat, hasHistoryFlag, historyFlagValue, historyFlagValueKnown: true)
            == ClipboardExclusionState.Excluded;
}
