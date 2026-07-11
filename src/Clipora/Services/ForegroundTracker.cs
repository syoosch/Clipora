using System;
using Clipora.Interop;

namespace Clipora.Services;

/// <summary>
/// 记录最近一个「非本应用」的前台窗口，供自动粘贴时还原目标。
/// 通过 SetWinEventHook 监听前台切换。
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    private readonly uint _ownProcessId;
    private readonly NativeMethods.WinEventDelegate _callback; // 保持引用，避免被 GC
    private IntPtr _hook;

    public IntPtr LastForeignWindow { get; private set; }

    public ForegroundTracker()
    {
        _ownProcessId = (uint)Environment.ProcessId;
        _callback = OnForegroundChanged;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            idProcess: 0,
            idThread: 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != _ownProcessId)
            LastForeignWindow = hwnd;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
