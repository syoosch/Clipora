namespace Clipora.Services;

/// <summary>
/// 托盘创建结果。
/// </summary>
public enum TrayCreateResult
{
    /// <summary>原生托盘图标创建成功。</summary>
    Success,
    /// <summary>创建失败（已写诊断日志）。</summary>
    Failure,
}

/// <summary>
/// 启动阶段对托盘创建结果的响应。
/// </summary>
public enum TrayStartupDecision
{
    /// <summary>按 SilentStart 设置正常决定是否显示主面板。</summary>
    ContinueNormal,
    /// <summary>托盘不可用时强制显示主面板，避免用户无任何可见界面。</summary>
    ForceShowPanel,
}

/// <summary>
/// 纯策略：托盘创建结果 → 启动阶段是否强制显示主面板。
/// 隔离自检使用的纯函数 seam。
/// </summary>
public static class TrayStartupPolicy
{
    /// <summary>
    /// 托盘成功 → 按 SilentStart 正常决定；托盘失败 → 必须强制显示主面板。
    /// </summary>
    public static TrayStartupDecision Decide(TrayCreateResult result)
    {
        return result == TrayCreateResult.Success
            ? TrayStartupDecision.ContinueNormal
            : TrayStartupDecision.ForceShowPanel;
    }
}
