namespace Clipora.Scrolling;

/// <summary>
/// 平滑滚动编排：把多个 ScrollViewer 的帧驱动缓动收口到一个深模块。
/// 替代原 HistoryWindow code-behind 中四套手搓引擎里的纵向滚轮与回到顶部两套。
/// </summary>
public interface ISmoothScroller
{
    /// <summary>滑动到绝对偏移。"回到顶部" = <c>GlideTo(surface, 0)</c>。</summary>
    void GlideTo(IScrollSurface surface, double targetOffset, ScrollGlide? options = null);

    /// <summary>按滚轮增量相对滑动，连续滚轮累加到当前活动目标。</summary>
    void GlideBy(IScrollSurface surface, double wheelDelta, ScrollGlide? options = null);

    /// <summary>取消单个面的滑动。</summary>
    void Cancel(IScrollSurface surface);

    /// <summary>取消所有滑动（任意鼠标按下时调用）。</summary>
    void CancelAll();
}
