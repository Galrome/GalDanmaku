using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GalDanmaku.App.Windows;

internal static class WindowLayer
{
    private const uint PositionFlags = NativeMethods.NoMovePosition | NativeMethods.NoSizePosition |
        NativeMethods.NoActivatePosition | NativeMethods.NoOwnerZOrderPosition;

    internal static void PlaceAboveTarget(nint window, nint target)
    {
        bool targetTopmost = IsTopmost(target);
        if (IsTopmost(window) != targetTopmost)
        {
            // HWND_TOP alone cannot remove an existing topmost band membership.
            Place(window, targetTopmost ? new nint(-1) : new nint(-2));
        }

        nint previous = NativeMethods.GetWindow(target, NativeMethods.PreviousWindow);
        while (previous != nint.Zero && IsOwnedBy(previous, window))
            previous = NativeMethods.GetWindow(previous, NativeMethods.PreviousWindow);
        if (previous == window) return;

        // A normal target can be first in its band, immediately below a topmost window.
        // In that case HWND_TOP keeps our normal band instead of inheriting the predecessor's band.
        if (previous == nint.Zero || (!targetTopmost && IsTopmost(previous))) previous = nint.Zero;
        Place(window, previous);
    }

    private static bool IsTopmost(nint window) =>
        ((long)NativeMethods.GetExtendedStyle(window) & NativeMethods.TopmostStyle) != 0;

    private static bool IsOwnedBy(nint window, nint owner)
    {
        for (nint current = NativeMethods.GetWindow(window, NativeMethods.OwnerWindow); current != nint.Zero;
             current = NativeMethods.GetWindow(current, NativeMethods.OwnerWindow))
            if (current == owner) return true;
        return false;
    }

    private static void Place(nint window, nint insertAfter)
    {
        if (!NativeMethods.SetWindowPos(window, insertAfter, 0, 0, 0, 0, PositionFlags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法同步目标窗口的显示层级。");
    }
}
