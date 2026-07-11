using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Clipora.Abstractions;
using Clipora.Interop;
using Clipora.Models;

namespace Clipora.Services;

/// <summary><see cref="IHotkeyService"/> 实现：用隐藏窗口接收 <c>WM_HOTKEY</c>，支持多动作多热键。</summary>
public sealed class HotkeyService : IHotkeyService
{
    private const int BaseId = 0xC10A;

    private HwndSource? _source;
    private readonly Dictionary<HotkeyAction, int> _registeredActions = new();

    public event EventHandler<HotkeyAction>? Pressed;

    public IReadOnlyList<HotkeyRegistration> RegisterAll(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> gestures)
    {
        UnregisterAll();
        EnsureWindow();

        var results = new List<HotkeyRegistration>();

        foreach (var (action, gesture) in gestures)
        {
            // 无效 gesture 跳过（不注册、不算失败）
            if (!gesture.IsValid)
                continue;

            int id = BaseId + (int)action;
            bool ok = NativeMethods.RegisterHotKey(
                _source!.Handle,
                id,
                gesture.Modifiers | NativeMethods.MOD_NOREPEAT,
                gesture.VirtualKey);

            if (ok)
                _registeredActions[action] = id;

            results.Add(new HotkeyRegistration(action, ok));
        }

        return results;
    }

    public void UnregisterAll()
    {
        if (_source is null) return;

        foreach (var (_, id) in _registeredActions)
            NativeMethods.UnregisterHotKey(_source.Handle, id);

        _registeredActions.Clear();
    }

    private void EnsureWindow()
    {
        if (_source is not null) return;

        var parameters = new HwndSourceParameters("CliporaHotkey")
        {
            Width = 0, Height = 0, WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            int actionOrdinal = id - BaseId;
            if (Enum.IsDefined(typeof(HotkeyAction), actionOrdinal))
            {
                Pressed?.Invoke(this, (HotkeyAction)actionOrdinal);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.Dispose();
        _source = null;
    }
}
