using System;
using System.Collections.Generic;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>保留期自动清理：删过期非置顶活动项，含数据库行与磁盘附属文件。</summary>
public sealed class RetentionCleanupService
{
    private readonly IClipStore _store;
    private readonly ClipItemFileEraser _eraser;
    private readonly ISettingsService _settings;

    public RetentionCleanupService(IClipStore store, ClipItemFileEraser eraser, ISettingsService settings)
    {
        _store = store;
        _eraser = eraser;
        _settings = settings;
    }

    /// <summary>清理条数 > 0 时触发，供 UI 刷新。</summary>
    public event EventHandler<int>? ItemsPurged;

    /// <summary>执行一次清理。返回清理条数；全程 try/catch，绝不抛。</summary>
    public int RunCleanup()
    {
        try
        {
            int retentionDays = _settings.Current.RetentionDays;
            IReadOnlyList<ClipItem> purged = _store.PurgeExpired(retentionDays);

            foreach (ClipItem item in purged)
            {
                // 逐项 try：单项文件清理异常（如畸形路径）不得中断其余项的清理。
                try { _eraser.Erase(item); }
                catch { /* best-effort，跳过该项 */ }
            }

            if (purged.Count > 0)
                ItemsPurged?.Invoke(this, purged.Count);

            return purged.Count;
        }
        catch
        {
            return 0;
        }
    }
}
