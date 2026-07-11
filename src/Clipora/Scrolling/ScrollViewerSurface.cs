using System;
using System.Windows.Controls;

namespace Clipora.Scrolling;

/// <summary>
/// 纵向 <see cref="ScrollViewer"/> 的生产适配器。
/// 以被包裹的 ScrollViewer 引用为身份做值相等，故每次事件可直接 new 一个，
/// <see cref="SmoothScroller"/> 内部状态字典仍按同一个 ScrollViewer 归并（连续滚轮累加因此成立）。
/// </summary>
public sealed class ScrollViewerSurface : IScrollSurface, IEquatable<ScrollViewerSurface>
{
    private readonly ScrollViewer _scrollViewer;

    public ScrollViewerSurface(ScrollViewer scrollViewer) => _scrollViewer = scrollViewer;

    public double Offset
    {
        get => _scrollViewer.VerticalOffset;
        set => _scrollViewer.ScrollToVerticalOffset(value);
    }

    public double ScrollableExtent => _scrollViewer.ScrollableHeight;

    public bool Equals(ScrollViewerSurface? other) =>
        other is not null && ReferenceEquals(_scrollViewer, other._scrollViewer);

    public override bool Equals(object? obj) => Equals(obj as ScrollViewerSurface);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_scrollViewer);
}
