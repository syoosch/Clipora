using System;
using System.Diagnostics;
using System.Windows.Media;

namespace Clipora.Scrolling;

/// <summary>
/// 基于 <see cref="CompositionTarget.Rendering"/> + <see cref="Stopwatch"/> 的生产帧时钟。
/// 跟随 WPF 实际渲染帧，而非 DispatcherTimer 猜测 16ms。
/// </summary>
public sealed class RenderingFrameClock : IFrameClock
{
    private bool _subscribed;

    public event Action? Tick;

    public long Timestamp => Stopwatch.GetTimestamp();

    public void Start()
    {
        if (_subscribed)
            return;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    public void Stop()
    {
        if (!_subscribed)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e) => Tick?.Invoke();
}
