using System;
using System.Collections.Generic;
using Clipora.Interop;

namespace Clipora.Models;

/// <summary>
/// 快捷键组合纯函数 seam：解析/格式化/合法性判定。不依赖 WPF 键盘状态。
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    // —— 主键 VK ↔ 字符串映射 ——
    private static readonly Dictionary<uint, string> VkToName = new()
    {
        // 字母
        [0x41] = "A", [0x42] = "B", [0x43] = "C", [0x44] = "D", [0x45] = "E",
        [0x46] = "F", [0x47] = "G", [0x48] = "H", [0x49] = "I", [0x4A] = "J",
        [0x4B] = "K", [0x4C] = "L", [0x4D] = "M", [0x4E] = "N", [0x4F] = "O",
        [0x50] = "P", [0x51] = "Q", [0x52] = "R", [0x53] = "S", [0x54] = "T",
        [0x55] = "U", [0x56] = "V", [0x57] = "W", [0x58] = "X", [0x59] = "Y",
        [0x5A] = "Z",
        // 数字
        [0x30] = "0", [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4",
        [0x35] = "5", [0x36] = "6", [0x37] = "7", [0x38] = "8", [0x39] = "9",
        // F 键
        [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4",
        [0x74] = "F5", [0x75] = "F6", [0x76] = "F7", [0x77] = "F8",
        [0x78] = "F9", [0x79] = "F10", [0x7A] = "F11", [0x7B] = "F12",
        // 符号/功能键
        [0x20] = "Space", [0x2E] = "Delete", [0x2D] = "Insert",
        [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
        [0x27] = "Right", [0x25] = "Left", [0x26] = "Up", [0x28] = "Down",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]",
        [0xDE] = "'",
    };

    private static readonly Dictionary<string, uint> NameToVk = new(StringComparer.OrdinalIgnoreCase);

    static HotkeyGesture()
    {
        foreach (var kv in VkToName)
            NameToVk[kv.Value] = kv.Key;
    }

    /// <summary>修饰键 token 映射（大小写不敏感）。</summary>
    private static readonly Dictionary<string, uint> ModTokenToFlag = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = NativeMethods.MOD_CONTROL,
        ["Control"] = NativeMethods.MOD_CONTROL,
        ["Alt"] = NativeMethods.MOD_ALT,
        ["Shift"] = NativeMethods.MOD_SHIFT,
        ["Win"] = NativeMethods.MOD_WIN,
        ["Windows"] = NativeMethods.MOD_WIN,
    };

    /// <summary>修饰键自身 VK 集合（IsValid 时排除纯修饰键）。</summary>
    private static readonly HashSet<uint> ModifierVks = new()
    {
        0x10, // Shift
        0x11, // Control
        0x12, // Alt (Menu)
        0x5B, // LWin
        0x5C, // RWin
    };

    /// <summary>至少一个修饰键 + 一个非修饰主键。</summary>
    public bool IsValid => Modifiers != 0
        && VirtualKey != 0
        && !ModifierVks.Contains(VirtualKey);

    /// <summary>
    /// 规范序格式化："Ctrl+Alt+Shift+Win+KEY"。无效 gesture 返回空串。
    /// </summary>
    public string Format()
    {
        if (!IsValid) return string.Empty;

        var parts = new List<string>(5);
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        if (VkToName.TryGetValue(VirtualKey, out string? name))
            parts.Add(name);
        else
            return string.Empty; // 无法序列化的主键

        return string.Join("+", parts);
    }

    /// <summary>
    /// 解析 "Ctrl+Shift+V" 格式字符串。失败返回 false。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        uint mods = 0;
        uint vk = 0;

        // 按 '+' 分割
        while (true)
        {
            int plusIdx = span.IndexOf('+');
            ReadOnlySpan<char> token;
            if (plusIdx >= 0)
            {
                token = span[..plusIdx].Trim();
                span = span[(plusIdx + 1)..];
            }
            else
            {
                token = span.Trim();
                span = default;
            }

            if (token.IsEmpty)
                break;

            string tokenStr = token.ToString();

            // 先尝试修饰键
            if (ModTokenToFlag.TryGetValue(tokenStr, out uint modFlag))
            {
                mods |= modFlag;
            }
            else if (NameToVk.TryGetValue(tokenStr, out uint keyVk))
            {
                if (vk != 0)
                    return false; // 多个主键不合法
                vk = keyVk;
            }
            else
            {
                return false; // 未知 token
            }

            if (plusIdx < 0) break;
        }

        gesture = new HotkeyGesture(mods, vk);
        return gesture.IsValid;
    }

    /// <summary>
    /// 由 WPF 键盘事件构造 gesture（UI 层转换，不进 Model 纯逻辑）。
    /// </summary>
    public static HotkeyGesture FromKeyInput(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        uint mods = 0;
        if (ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (alt) mods |= NativeMethods.MOD_ALT;
        if (shift) mods |= NativeMethods.MOD_SHIFT;
        if (win) mods |= NativeMethods.MOD_WIN;

        return new HotkeyGesture(mods, (uint)virtualKey);
    }
}
