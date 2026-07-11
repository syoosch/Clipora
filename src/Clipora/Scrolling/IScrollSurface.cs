namespace Clipora.Scrolling;

/// <summary>
/// 「能滚动的东西」的抽象接口（内部接缝）。
/// 生产实现包裹 <see cref="System.Windows.Controls.ScrollViewer"/>；测试可用假对象驱动。
/// 轴向（纵向/横向）由具体适配器决定，不进接口。
/// </summary>
public interface IScrollSurface
{
    /// <summary>当前滚动偏移（读=当前位置，写=滚动到该位置）。</summary>
    double Offset { get; set; }

    /// <summary>可滚动范围（生产为 ScrollableHeight / ScrollableWidth）。</summary>
    double ScrollableExtent { get; }
}
