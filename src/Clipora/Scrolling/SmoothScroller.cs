using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Clipora.Scrolling;

/// <summary>
/// 平滑滚动编排实现。每个面维护一份缓动状态 + 共享一个帧时钟订阅（同原 code-behind 行为）。
/// 对 <see cref="IScrollSurface"/> 与 <see cref="IFrameClock"/> 两个接缝工作，便于在 --selftest 中用假对象驱动。
/// </summary>
public sealed class SmoothScroller : ISmoothScroller
{
    private sealed class GlideState
    {
        public double StartOffset;
        public double TargetOffset;
        public long StartTimestamp;
        public double DurationMs;
        /// <summary>true=GlideBy（滚轮，连续累加目标）；false=GlideTo（绝对，如回到顶部）。</summary>
        public bool IsWheel;
        public bool IsActive;
    }

    private readonly IFrameClock _clock;
    private readonly Dictionary<IScrollSurface, GlideState> _states = new();
    private bool _clockRunning;

    public SmoothScroller(IFrameClock clock)
    {
        _clock = clock;
        _clock.Tick += OnTick;
    }

    public void GlideBy(IScrollSurface surface, double wheelDelta, ScrollGlide? options = null)
    {
        if (surface.ScrollableExtent <= 0 || wheelDelta == 0)
            return;

        ScrollGlide glide = options ?? ScrollGlide.Wheel;
        double current = surface.Offset;
        GlideState state = GetOrAdd(surface);

        // 连续滚轮输入累加目标，但每次从当前画面位置重新缓动，避免动画排队或反向顿挫。
        // 仅当上一次也是滚轮（GlideBy）时才累加；若上一次是 GlideTo（如回到顶部）被打断，从当前位置重起。
        double baseTarget = state is { IsActive: true, IsWheel: true } ? state.TargetOffset : current;
        double deltaPixels = wheelDelta / 120.0 * glide.PixelsPerNotch;

        state.StartOffset = current;
        state.TargetOffset = System.Math.Clamp(baseTarget - deltaPixels, 0, surface.ScrollableExtent);
        state.StartTimestamp = _clock.Timestamp;
        state.DurationMs = glide.DurationMs;
        state.IsWheel = true;
        state.IsActive = true;
        EnsureClock();
    }

    public void GlideTo(IScrollSurface surface, double targetOffset, ScrollGlide? options = null)
    {
        ScrollGlide glide = options ?? ScrollGlide.Wheel;
        double current = surface.Offset;
        GlideState state = GetOrAdd(surface);

        double extent = surface.ScrollableExtent;
        state.StartOffset = current;
        state.TargetOffset = System.Math.Clamp(targetOffset, 0, extent < 0 ? 0 : extent);
        state.StartTimestamp = _clock.Timestamp;
        state.DurationMs = glide.DurationMs;
        state.IsWheel = false;
        state.IsActive = true;
        EnsureClock();
    }

    public void Cancel(IScrollSurface surface)
    {
        if (_states.TryGetValue(surface, out GlideState? state))
            state.IsActive = false;
    }

    public void CancelAll()
    {
        foreach (GlideState state in _states.Values)
            state.IsActive = false;
        StopClock();
    }

    private GlideState GetOrAdd(IScrollSurface surface)
    {
        if (!_states.TryGetValue(surface, out GlideState? state))
        {
            state = new GlideState();
            _states.Add(surface, state);
        }
        return state;
    }

    private void OnTick()
    {
        long now = _clock.Timestamp;
        bool anyActive = false;

        foreach ((IScrollSurface surface, GlideState state) in _states.ToArray())
        {
            if (!state.IsActive)
                continue;

            double elapsedMs = (now - state.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            double offset = ScrollEasing.OffsetAt(state.StartOffset, state.TargetOffset, elapsedMs, state.DurationMs);
            surface.Offset = offset;

            if (elapsedMs >= state.DurationMs)
                state.IsActive = false;
            else
                anyActive = true;
        }

        if (!anyActive)
            StopClock();
    }

    private void EnsureClock()
    {
        if (_clockRunning)
            return;
        _clock.Start();
        _clockRunning = true;
    }

    private void StopClock()
    {
        if (!_clockRunning)
            return;
        _clock.Stop();
        _clockRunning = false;
    }
}
