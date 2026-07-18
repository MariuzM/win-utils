using WinSnip.Native;
using static WinSnip.Native.Win32Const;

namespace WinSnip.Ui;

// Finds the top-level window under a screen point.
//
// Deliberately not WindowFromPoint: the selection overlay covers the whole virtual desktop, so
// WindowFromPoint would only ever return the overlay itself. Making the overlay click-through with
// WS_EX_TRANSPARENT would fix that but would also stop it receiving the mouse messages it needs.
// EnumWindows walks top-level windows in z-order, so the first hit is the topmost window.
internal static unsafe class WindowPicker
{
    private static POINT _probe;
    private static IntPtr _exclude;
    private static IntPtr _hit;
    private static RECT _hitBounds;

    public static bool TryPick(POINT screenPoint, IntPtr exclude, out IntPtr hwnd, out RECT bounds)
    {
        // EnumWindows takes a function pointer with no managed context, so the query has to travel
        // through static state. Captures are user-driven and strictly one at a time, so there is no
        // concurrency to guard against here.
        _probe = screenPoint;
        _exclude = exclude;
        _hit = IntPtr.Zero;
        _hitBounds = default;

        Win32.EnumWindows(&OnWindow, IntPtr.Zero);

        hwnd = _hit;
        bounds = _hitBounds;
        return _hit != IntPtr.Zero;
    }

    public static bool TryBounds(IntPtr hwnd, out RECT bounds)
    {
        // Extended frame bounds rather than GetWindowRect: on Windows 10 and 11 GetWindowRect
        // includes the invisible resize border, which would leave a strip of whatever is behind the
        // window around every edge of the capture.
        if (Win32.DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, sizeof(RECT)) == 0
            && bounds.Width > 0
            && bounds.Height > 0)
        {
            return true;
        }

        return Win32.GetWindowRect(hwnd, out bounds) && bounds.Width > 0 && bounds.Height > 0;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int OnWindow(IntPtr hwnd, IntPtr lParam)
    {
        // An exception must never unwind into the OS enumerator, and one unreadable window should
        // not abort the sweep.
        try
        {
            if (_hit != IntPtr.Zero)
                return 0;

            if (!IsPickable(hwnd))
                return 1;

            if (!TryBounds(hwnd, out RECT bounds))
                return 1;

            if (bounds.Contains(_probe.X, _probe.Y))
            {
                _hit = hwnd;
                _hitBounds = bounds;
                return 0;
            }
        }
        catch
        {
        }

        return 1;
    }

    private static bool IsPickable(IntPtr hwnd)
    {
        if (hwnd == _exclude || !Win32.IsWindowVisible(hwnd))
            return false;

        // Owned windows are dialogs and popups belonging to another window; capturing one on its
        // own is almost never what a click meant.
        if (Win32.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            return false;

        // A minimized window has nothing composited to capture - WGC would return an empty frame.
        long style = (long)Win32.GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_MINIMIZE) != 0)
            return false;

        long exStyle = (long)Win32.GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return false;

        // Cloaked covers the big invisible-but-visible case: suspended UWP apps and windows parked
        // on another virtual desktop both report IsWindowVisible = true.
        if (Win32.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (Win32.GetWindowTitle(hwnd).Length == 0)
            return false;

        return Win32.GetWindowClassName(hwnd) switch
        {
            "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => false,
            "Windows.UI.Core.CoreWindow" or "ApplicationManager_DesktopShellWindow" => false,
            _ => true,
        };
    }
}
