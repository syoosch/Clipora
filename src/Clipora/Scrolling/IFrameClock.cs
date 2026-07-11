using System;

namespace Clipora.Scrolling;

/// <summary>
/// 「帧节拍 + 时间戳」的抽象接口（内部接缝）。
/// 生产实现基于 <see cref="System.Windows.Media.CompositionTarget.Rendering"/> + <see cref="System.Diagnostics.Stopwatch"/>；
/// 测试可手动节拍并喂入确定性时间戳。
/// </summary>
public interface IFrameClock
{
    /// <summary>每帧触发一次。</summary>
    event Action? Tick;

    /// <summary>高精度时间戳（与 <see cref="System.Diagnostics.Stopwatch.Frequency"/> 同刻度）。</summary>
    long Timestamp { get; }

    /// <summary>开始产生帧节拍（幂等）。</summary>
    void Start();

    /// <summary>停止帧节拍（幂等）。</summary>
    void Stop();
}
