using System;
using System.Collections.Generic;

namespace Clipora.Services;

/// <summary>
/// 顺序粘贴 burst 计算纯函数。由最近历史按 CreatedAt 降序输入，算出含最新一条的连续"复制爆发"批次下标（oldest→newest）。
/// </summary>
public static class SequentialPasteBurstPlanner
{
    /// <summary>
    /// <paramref name="createdAtDescending"/> 按 CreatedAt 降序（最新在前）传入。
    /// 从 index 0 向后扫描：相邻间隔 ≤ <paramref name="gapSeconds"/> 则同批；一旦 > gap 即断开。
    /// 返回最近一批的下标序列，已按 oldest→newest 排列。空输入→空列表。
    /// </summary>
    public static IReadOnlyList<int> ComputeMostRecentBurst(
        IReadOnlyList<DateTime> createdAtDescending,
        double gapSeconds)
    {
        int n = createdAtDescending.Count;
        if (n == 0) return Array.Empty<int>();

        // 从最新(0)向后找断开点
        int burstEnd = 0; // 包括 [0..burstEnd] 都在同批
        for (int i = 1; i < n; i++)
        {
            // i-1 更旧（时间上更早），i 更新；CreatedAt 降序表示 createdAt[i-1] >= createdAt[i]
            double gap = (createdAtDescending[i - 1] - createdAtDescending[i]).TotalSeconds;
            if (gap > gapSeconds)
                break;
            burstEnd = i;
        }

        // 收集 [0..burstEnd] 反转 oldest→newest
        var result = new int[burstEnd + 1];
        for (int i = 0; i <= burstEnd; i++)
            result[i] = burstEnd - i;

        return result;
    }
}
