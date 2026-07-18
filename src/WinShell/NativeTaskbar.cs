using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// Hides the built-in Windows 11 taskbar without killing Explorer.
///
/// Explorer stays alive deliberately: the taskbar is only a window (Shell_TrayWnd) inside
/// explorer.exe, which also owns the desktop, File Explorer, and the shell notification
/// plumbing that other apps depend on. Terminating it to remove a window would take all of
/// that with it. Hiding the window gets the same visual result and is instantly reversible.
///
/// Explorer re-shows the tray window on its own after certain events (display changes,
/// Explorer restarts, some full-screen transitions), so <see cref="Reapply"/> is cheap and
/// designed to be called from the taskbar's existing timer rather than from a polling loop.
/// </summary>
internal static unsafe class NativeTaskbar
{
    private const string PrimaryClass = "Shell_TrayWnd";
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";

    // Populated by the EnumWindows callback below. The callback has to be a static function
    // pointer to stay AOT-safe, so it cannot capture - it writes here instead. Enumeration is
    // single-threaded (message loop only), so a plain static list is sufficient.
    private static readonly List<IntPtr> Discovered = new();

    private static bool _suppressing;

    public static bool Suppressing => _suppressing;

    /// <summary>Hides every tray window on every monitor. Safe to call repeatedly.</summary>
    public static void Hide()
    {
        _suppressing = true;
        foreach (IntPtr hwnd in FindTrayWindows())
        {
            Win32.ShowWindow(hwnd, SW_HIDE);
        }
    }

    /// <summary>
    /// Re-hides any tray window Explorer has brought back. No-op once <see cref="Restore"/>
    /// has run, so shutdown does not race against the timer.
    /// </summary>
    public static void Reapply()
    {
        if (!_suppressing)
            return;

        foreach (IntPtr hwnd in FindTrayWindows())
        {
            if (Win32.IsWindowVisible(hwnd))
                Win32.ShowWindow(hwnd, SW_HIDE);
        }
    }

    /// <summary>
    /// Brings the native taskbar back. This must run on every exit path - a crash that left
    /// the user with no taskbar at all and no obvious way to recover would be far worse than
    /// the bug that caused it.
    /// </summary>
    public static void Restore()
    {
        _suppressing = false;
        foreach (IntPtr hwnd in FindTrayWindows())
        {
            Win32.ShowWindow(hwnd, SW_SHOW);
        }
    }

    /// <summary>
    /// Releases desktop edge space left reserved by an appbar whose process died without
    /// calling ABM_REMOVE. A force kill skips both `finally` and ProcessExit, and the shell
    /// does not always reclaim the strip on its own - the symptom is a taskbar that docks
    /// progressively higher up the screen on each run, above a gap nothing is drawing in.
    /// Resetting the work area to the full screen makes the remaining appbars re-assert their
    /// own claims via the SPIF_SENDCHANGE broadcast.
    /// </summary>
    public static void ResetWorkArea()
    {
        var full = new RECT(
            0, 0,
            Win32.GetSystemMetrics(SM_CXSCREEN),
            Win32.GetSystemMetrics(SM_CYSCREEN));

        Win32.SystemParametersInfo(SPI_SETWORKAREA, 0, ref full, SPIF_SENDCHANGE);
    }

    private static List<IntPtr> FindTrayWindows()
    {
        Discovered.Clear();

        // FindWindow first: it is a direct lookup and always finds the primary bar, even in
        // the rare case where enumeration misses it mid-Explorer-restart.
        IntPtr primary = Win32.FindWindow(PrimaryClass, null);
        if (primary != IntPtr.Zero)
            Discovered.Add(primary);

        // Secondary bars (one per additional monitor) have no fixed handle, so they have to
        // be enumerated.
        Win32.EnumWindows(&OnEnumWindow, IntPtr.Zero);

        return Discovered;
    }

    [UnmanagedCallersOnly]
    private static int OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        // An exception escaping into native code would tear down the process, so this whole
        // callback is defensive. Returning 1 continues enumeration.
        try
        {
            string className = Win32.GetWindowClass(hwnd);
            if ((className == PrimaryClass || className == SecondaryClass) && !Discovered.Contains(hwnd))
            {
                Discovered.Add(hwnd);
            }
        }
        catch
        {
            // Skip this window rather than abort the sweep.
        }

        return 1;
    }
}
