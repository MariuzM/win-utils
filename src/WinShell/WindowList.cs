using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal sealed class TaskWindow
{
    public IntPtr Handle;
    public string Title = string.Empty;
    public IntPtr Icon;
    public bool IsMinimized;

    // Hit-rectangle in taskbar client coordinates, assigned during layout so mouse handling
    // and painting agree on where each button is without computing it twice.
    public RECT Bounds;
}

/// <summary>
/// Tracks the set of windows that belong on the taskbar.
///
/// Deliberately event-driven rather than polled: a 250 ms enumeration timer is the single
/// biggest reason naive taskbar replacements burn CPU while the machine is idle. SetWinEventHook
/// wakes us only when a window is actually created, destroyed, shown, hidden, renamed, or
/// activated, so idle cost is genuinely zero.
///
/// The hook fires for every window on the desktop, and bursts (an app opening spawns several
/// events in a row), so it does no work inline - it coalesces into a single posted message that
/// the taskbar drains once.
/// </summary>
internal static unsafe class WindowList
{
    public static readonly List<TaskWindow> Items = new();

    private static readonly List<IntPtr> Candidates = new();
    private static readonly List<IntPtr> Hooks = new();

    // Icons are cached per window handle. Without this, every refresh sends WM_GETICON to
    // every window on the desktop - and refreshes are frequent, because any app that animates
    // its title bar (a terminal showing elapsed time, a browser showing load progress) fires
    // EVENT_OBJECT_NAMECHANGE once a second. A cross-process SendMessageTimeout per window per
    // second is easily the most expensive thing this process could be doing at idle.
    private static readonly Dictionary<IntPtr, IntPtr> IconCache = new();

    private static IntPtr _notifyHwnd;
    private static uint _notifyMessage;
    private static uint _ownProcessId;

    // Set from the hook, cleared when the taskbar drains it. Guards against flooding the
    // message queue during event bursts.
    private static volatile bool _refreshPending;

    public static void Initialize(IntPtr notifyHwnd, uint notifyMessage)
    {
        _notifyHwnd = notifyHwnd;
        _notifyMessage = notifyMessage;
        _ownProcessId = (uint)Environment.ProcessId;

        // Each range is kept as narrow as possible. Subscribing to one broad span would pull
        // in EVENT_SYSTEM_SCROLLINGSTART/END, MOVESIZESTART/END, CAPTURESTART/END and the
        // menu and alert events - all of which fire heavily during ordinary interaction and
        // none of which change the window list.
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);

        Refresh();
    }

    private static void Hook(uint min, uint max)
    {
        IntPtr hook = Win32.SetWinEventHook(
            min,
            max,
            IntPtr.Zero,
            &OnWinEvent,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        );

        if (hook != IntPtr.Zero)
            Hooks.Add(hook);
    }

    public static void Shutdown()
    {
        foreach (IntPtr hook in Hooks)
            Win32.UnhookWinEvent(hook);

        Hooks.Clear();
    }

    /// <summary>Called by the taskbar when it drains the coalesced refresh message.</summary>
    public static void ClearPending() => _refreshPending = false;

    [UnmanagedCallersOnly]
    private static void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint thread,
        uint time
    )
    {
        try
        {
            // idObject != OBJID_WINDOW filters out events about controls, menu items, carets
            // and scrollbars inside a window - we only care about the window itself.
            if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero)
                return;

            if (_refreshPending)
                return;

            _refreshPending = true;
            Win32.PostMessage(_notifyHwnd, _notifyMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Never let an exception unwind into the OS hook dispatcher.
        }
    }

    /// <summary>
    /// Rebuilds the list while keeping existing buttons where they are.
    ///
    /// EnumWindows returns windows in Z-order, so rebuilding the list from it directly would
    /// reorder the bar every time the user switched windows - activating an app moves it to the
    /// front of the Z-order and its button would jump to the first slot. Buttons must hold
    /// their position regardless of which window is active, so entries already on the bar keep
    /// their index and only genuinely new windows are appended.
    /// </summary>
    public static void Refresh()
    {
        Candidates.Clear();
        Win32.EnumWindows(&OnEnumWindow, IntPtr.Zero);

        PruneIconCache();

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!Candidates.Contains(Items[i].Handle))
                Items.RemoveAt(i);
        }

        foreach (IntPtr hwnd in Candidates)
        {
            TaskWindow? existing = null;
            foreach (TaskWindow item in Items)
            {
                if (item.Handle == hwnd)
                {
                    existing = item;
                    break;
                }
            }

            if (existing == null)
            {
                Items.Add(
                    new TaskWindow
                    {
                        Handle = hwnd,
                        Title = Win32.GetWindowTitle(hwnd),
                        Icon = GetIcon(hwnd),
                        IsMinimized = Win32.IsIconic(hwnd),
                    }
                );

                continue;
            }

            existing.Title = Win32.GetWindowTitle(hwnd);
            existing.Icon = GetIcon(hwnd);
            existing.IsMinimized = Win32.IsIconic(hwnd);
        }
    }

    /// <summary>
    /// Drops cache entries for windows that no longer exist, so the dictionary cannot grow
    /// without bound over a long session. The icon handles themselves are owned by the target
    /// application, so they must not be destroyed here - only forgotten.
    /// </summary>
    private static void PruneIconCache()
    {
        if (IconCache.Count <= Candidates.Count)
            return;

        List<IntPtr>? stale = null;
        foreach (IntPtr hwnd in IconCache.Keys)
        {
            if (!Win32.IsWindow(hwnd))
                (stale ??= new List<IntPtr>()).Add(hwnd);
        }

        if (stale is null)
            return;

        foreach (IntPtr hwnd in stale)
            IconCache.Remove(hwnd);
    }

    [UnmanagedCallersOnly]
    private static int OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (ShouldAppearOnTaskbar(hwnd))
                Candidates.Add(hwnd);
        }
        catch
        {
            // Skip this window rather than abort the sweep.
        }

        return 1;
    }

    /// <summary>
    /// The standard "is this an Alt-Tab window" test, plus the DWM cloaking check that
    /// Windows 10/11 made mandatory.
    /// </summary>
    private static bool ShouldAppearOnTaskbar(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd))
            return false;

        // Owned windows are dialogs and palettes belonging to a window that already has a
        // button; they must not get one of their own.
        if (Win32.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            return false;

        long exStyle = (long)Win32.GetWindowLongPtr(hwnd, GWL_EXSTYLE);

        // WS_EX_APPWINDOW is an explicit request for a taskbar button and overrides the
        // tool-window exclusion below.
        bool forcesButton = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!forcesButton && (exStyle & WS_EX_TOOLWINDOW) != 0)
            return false;

        // Suspended UWP apps stay "visible" forever but are cloaked by DWM. Without this
        // check the bar fills up with phantom buttons for apps that are not really running -
        // Calculator, Settings, Photos and friends all linger this way on Windows 11.
        if (Win32.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (Win32.GetWindowTitle(hwnd).Length == 0)
            return false;

        // Our own taskbar and Start menu must never list themselves.
        Win32.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == _ownProcessId)
            return false;

        // Explorer's desktop and shell surfaces are windows like any other, so exclude them
        // by class.
        string className = Win32.GetWindowClass(hwnd);
        if (
            className
            is "Progman"
                or "WorkerW"
                or "Shell_TrayWnd"
                or "Shell_SecondaryTrayWnd"
                or "Windows.UI.Core.CoreWindow"
                or "ApplicationManager_DesktopShellWindow"
        )
            return false;

        return true;
    }

    /// <summary>
    /// Fetches a window's icon. Uses SendMessageTimeout rather than SendMessage because
    /// WM_GETICON is handled on the target app's UI thread - a hung app would otherwise block
    /// the taskbar indefinitely, which is exactly the freeze users notice most.
    /// </summary>
    private static IntPtr GetIcon(IntPtr hwnd)
    {
        if (IconCache.TryGetValue(hwnd, out IntPtr cached))
            return cached;

        IntPtr icon = FetchIcon(hwnd);
        IconCache[hwnd] = icon;
        return icon;
    }

    private static IntPtr FetchIcon(IntPtr hwnd)
    {
        foreach (int type in stackalloc[] { ICON_SMALL2, ICON_SMALL, ICON_BIG })
        {
            Win32.SendMessageTimeout(
                hwnd,
                WM_GETICON,
                new IntPtr(type),
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                50,
                out IntPtr result
            );

            if (result != IntPtr.Zero)
                return result;
        }

        // Falling back to the window class icon covers apps that never respond to WM_GETICON.
        IntPtr classIcon = Win32.GetClassLongPtr(hwnd, GCLP_HICONSM);
        if (classIcon != IntPtr.Zero)
            return classIcon;

        return Win32.GetClassLongPtr(hwnd, GCLP_HICON);
    }

    /// <summary>
    /// Click behaviour matching Windows 7: activate the window, or minimise it if it is
    /// already the foreground window.
    /// </summary>
    public static void Activate(TaskWindow window)
    {
        if (!Win32.IsWindow(window.Handle))
            return;

        if (Win32.GetForegroundWindow() == window.Handle)
        {
            Win32.ShowWindowAsync(window.Handle, SW_MINIMIZE);
            return;
        }

        if (Win32.IsIconic(window.Handle))
            Win32.ShowWindowAsync(window.Handle, SW_RESTORE);

        Win32.SetForegroundWindow(window.Handle);
    }
}
