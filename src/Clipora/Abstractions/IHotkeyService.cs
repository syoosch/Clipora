using System;
using System.Collections.Generic;
using Clipora.Models;

namespace Clipora.Abstractions;

/// <summary>快捷键动作标识。</summary>
public enum HotkeyAction { OpenPanel, PastePlain, SequentialPaste }

/// <summary>单次热键注册结果。</summary>
public readonly record struct HotkeyRegistration(HotkeyAction Action, bool Succeeded);

/// <summary>全局快捷键服务：按动作注册多热键，每个独立 hotkey id，Pressed 带动作标识。</summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>先全部注销再按字典注册；gesture 为 null/无效的动作跳过（不注册、不算失败）。返回每个尝试注册动作的成功/失败。</summary>
    IReadOnlyList<HotkeyRegistration> RegisterAll(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> gestures);

    void UnregisterAll();

    event EventHandler<HotkeyAction>? Pressed;
}
