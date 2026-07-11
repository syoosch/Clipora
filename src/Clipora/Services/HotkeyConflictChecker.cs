using System;
using System.Collections.Generic;
using Clipora.Abstractions;
using Clipora.Models;

namespace Clipora.Services;

/// <summary>快捷键应用内冲突检测纯函数。</summary>
public static class HotkeyConflictChecker
{
    /// <summary>
    /// 返回与其它动作绑定到同一 gesture（mods+vk 全等）的动作集合。
    /// 无效 gesture 跳过，不与任何动作视为冲突。
    /// </summary>
    public static IReadOnlyList<HotkeyAction> FindDuplicates(
        IReadOnlyDictionary<HotkeyAction, HotkeyGesture> gestures)
    {
        var duplicates = new List<HotkeyAction>();
        // 按 (Modifiers, VirtualKey) 分组
        var groupByGesture = new Dictionary<(uint, uint), List<HotkeyAction>>();

        foreach (var (action, gesture) in gestures)
        {
            if (!gesture.IsValid)
                continue;

            var key = (gesture.Modifiers, gesture.VirtualKey);
            if (!groupByGesture.TryGetValue(key, out var list))
            {
                list = new List<HotkeyAction>();
                groupByGesture[key] = list;
            }
            list.Add(action);
        }

        // 收集所有组内 >1 个动作的冲突
        foreach (var (_, actions) in groupByGesture)
        {
            if (actions.Count > 1)
                duplicates.AddRange(actions);
        }

        return duplicates;
    }
}
