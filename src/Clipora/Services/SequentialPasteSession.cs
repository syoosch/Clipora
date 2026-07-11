using System;
using System.Collections.Generic;

namespace Clipora.Services;

/// <summary>顺序粘贴步骤结果：ItemId=应粘的稳定 ClipItem Id；IsFirstOfSession=本步是新一轮的首项。</summary>
public readonly record struct SequentialPasteStep(long ItemId, bool IsFirstOfSession);

/// <summary>
/// 顺序粘贴状态机（纯逻辑，不碰剪贴板/UI）。可注入"当前时间"便于自检。
/// </summary>
public sealed class SequentialPasteSession
{
    private readonly double _idleSeconds;
    private bool _active;
    private bool _completed;
    private long[]? _batch;              // oldest→newest 的稳定 ClipItem Id
    private int _index;                   // 下一次要粘的 batch 下标
    private DateTime _lastPressTime;      // 上次按键时间

    /// <param name="idleSeconds">空闲超时秒数（默认 60）</param>
    public SequentialPasteSession(double idleSeconds = 60)
    {
        _idleSeconds = idleSeconds;
    }

    /// <summary>
    /// 按键处理。传入当前 burst（oldest→newest 的稳定 ClipItem Id）与当前时间。
    /// 返回应粘的 ClipItem Id，或 null 表示无可粘。
    /// </summary>
    public SequentialPasteStep? Press(IReadOnlyList<long> mostRecentBurst, DateTime now)
    {
        if (mostRecentBurst.Count == 0)
        {
            Reset();
            return null;
        }

        double idleSince = _active ? (now - _lastPressTime).TotalSeconds : double.MaxValue;
        bool burstChanged = _batch is not null && !BatchEquals(mostRecentBurst);

        // 已粘完的同一批次保持耗尽；继续按键只 no-op，不能从头循环。
        if (_completed && !burstChanged)
            return null;

        // 首次、burst 变化，或尚未完成的会话空闲超时才重新开批。
        bool shouldRestart = _batch is null
            || burstChanged
            || (_active && idleSince > _idleSeconds);

        if (shouldRestart)
        {
            _batch = new long[mostRecentBurst.Count];
            for (int i = 0; i < mostRecentBurst.Count; i++)
                _batch[i] = mostRecentBurst[i];
            _index = 1; // 0 即将作为 IsFirstOfSession 返回，下次从下标 1 继续
            _completed = _batch.Length == 1;
            _active = !_completed;
            _lastPressTime = now;
            return new SequentialPasteStep(_batch[0], IsFirstOfSession: true);
        }

        // 继续当前批次
        long itemId = _batch![_index];
        _index++;
        _lastPressTime = now;

        // 检查是否完成
        if (_index >= _batch.Length)
        {
            _active = false;
            _completed = true;
        }

        return new SequentialPasteStep(itemId, IsFirstOfSession: false);
    }

    /// <summary>用户复制了新内容 → 重置会话，下次按键重新计算 burst。</summary>
    public void Reset()
    {
        _active = false;
        _completed = false;
        _batch = null;
        _index = 0;
    }

    private bool BatchEquals(IReadOnlyList<long> candidate)
    {
        if (_batch is null || _batch.Length != candidate.Count)
            return false;

        for (int i = 0; i < _batch.Length; i++)
        {
            if (_batch[i] != candidate[i])
                return false;
        }

        return true;
    }
}
