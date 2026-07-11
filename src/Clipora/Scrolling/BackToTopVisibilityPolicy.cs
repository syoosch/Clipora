namespace Clipora.Scrolling;

/// <summary>「回到顶部」按钮在一次滚动事件后应执行的动作。</summary>
public enum BackToTopAction
{
    None,
    Show,
    Hide,
}

/// <summary>
/// 「回到顶部」按钮显隐的纯策略（无 WPF 依赖，可直接进 --selftest）。
/// 行为保持型重构：阈值与滞回规则逐字沿用原 <c>ItemsList_ScrollChanged</c>。
/// </summary>
public static class BackToTopVisibilityPolicy
{
    /// <summary>上滚累计达到此像素距离才浮出按钮。</summary>
    public const double ShowUpThreshold = 64;
    /// <summary>浮出后下滚累计达到此值即收起（"往下滚一点距离"）。</summary>
    public const double HideDownThreshold = 32;
    /// <summary>距顶不足此偏移量时不浮出（避免刚离顶就出现）。</summary>
    public const double MinOffset = 160;

    /// <summary>
    /// 根据本次滚动变化量、当前垂直偏移与按钮可见性，决定显隐动作并更新累加器。
    /// <paramref name="upAccum"/> / <paramref name="downAccum"/> 为调用方持有的滞回累加状态，按引用更新。
    /// 返回 <see cref="BackToTopAction.None"/> 表示无需改变（已与目标可见性一致或未达阈值）。
    /// </summary>
    public static BackToTopAction Decide(
        double verticalChange,
        double verticalOffset,
        bool currentlyVisible,
        ref double upAccum,
        ref double downAccum)
    {
        // 近顶：清零累加并收起（原代码无条件 HideBackToTop，幂等）。
        if (verticalOffset <= 0.5)
        {
            upAccum = 0;
            downAccum = 0;
            return currentlyVisible ? BackToTopAction.Hide : BackToTopAction.None;
        }

        if (verticalChange < 0)
        {
            upAccum += -verticalChange;
            downAccum = 0;
        }
        else if (verticalChange > 0)
        {
            downAccum += verticalChange;
            upAccum = 0;
        }

        // 与原 if / else-if 顺序一致：下滚收起优先于上滚浮出。
        if (downAccum >= HideDownThreshold)
            return currentlyVisible ? BackToTopAction.Hide : BackToTopAction.None;

        if (upAccum >= ShowUpThreshold && verticalOffset >= MinOffset)
            return currentlyVisible ? BackToTopAction.None : BackToTopAction.Show;

        return BackToTopAction.None;
    }
}
