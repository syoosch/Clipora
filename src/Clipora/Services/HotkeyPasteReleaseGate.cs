using System;
using Clipora.Interop;

namespace Clipora.Services;

/// <summary>判断触发全局热键的主键与修饰键是否已全部释放。</summary>
public static class HotkeyPasteReleaseGate
{
    public static bool AreReleased(uint mainVirtualKey, Func<int, bool> isKeyDown)
    {
        ArgumentNullException.ThrowIfNull(isKeyDown);

        if (mainVirtualKey != 0 && isKeyDown((int)mainVirtualKey))
            return false;

        return !isKeyDown(NativeMethods.VK_CONTROL)
            && !isKeyDown(NativeMethods.VK_SHIFT)
            && !isKeyDown(NativeMethods.VK_MENU)
            && !isKeyDown(NativeMethods.VK_LWIN)
            && !isKeyDown(NativeMethods.VK_RWIN);
    }
}
