using System;
using System.Windows;
using System.Windows.Interop;
using Clipora.Interop;

namespace Clipora.Services;

/// <summary>外部拖入收起判定的共享原语：主面板拖入浮层与悬浮球拖入方块共用，
/// 避免 OLE 不保证回投终结事件时浮层/方块卡死。</summary>
internal static class DragWindowHelpers
{
    /// <summary>用实时物理光标位置比对窗口矩形，判断光标是否真的在窗口内。
    /// 拖动经过子元素时 WPF 的 <c>e.GetPosition</c> 会偶发过时/越界坐标，故拖入收起判定一律走此法。</summary>
    public static bool IsCursorInsideWindow(Window window)
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            return false;

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect))
            return false;

        return cursor.X >= rect.Left && cursor.X < rect.Right
            && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    /// <summary>物理左键是否仍按下（= 拖拽是否仍在进行）。OLE 在拖动悬停静止时停发 DragOver，
    /// 故看门狗用此真实状态作闸门：松开才判定拖拽确已结束。</summary>
    public static bool IsPhysicalLeftButtonDown()
        => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
}
