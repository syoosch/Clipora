using System;

namespace Clipora.Scrolling;

/// <summary>
/// 缓动与时间推进的纯函数（无 WPF 依赖，可直接进 --selftest）。
/// 行为保持型重构：数学逐字沿用原 HistoryWindow code-behind 的 ease-out cubic。
/// </summary>
public static class ScrollEasing
{
    /// <summary>ease-out cubic：0→0、1→1，单调递增。等价于原代码 <c>1 - (1-p)^3</c>。</summary>
    public static double EaseOutCubic(double progress)
    {
        double remaining = 1 - progress;
        return 1 - remaining * remaining * remaining;
    }

    /// <summary>
    /// 给定起点/终点与已用/总时长，返回当前 offset。
    /// 到时（elapsed ≥ duration）精确停在 <paramref name="target"/>，过程不过冲。
    /// </summary>
    public static double OffsetAt(double start, double target, double elapsedMs, double durationMs)
    {
        if (durationMs <= 0)
            return target;
        double progress = Math.Clamp(elapsedMs / durationMs, 0, 1);
        double eased = EaseOutCubic(progress);
        return start + (target - start) * eased;
    }
}
