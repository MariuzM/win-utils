using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// The taskbar itself: a plain Win32 window, painted with GDI, docked to the bottom of the
/// primary monitor as a registered appbar.
///
/// Registering as an appbar (rather than just parking a topmost window at the bottom of the
/// screen) is what makes maximised windows stop above the bar instead of underneath it. The
/// shell tracks reserved edge space per appbar; without it, every maximised window would cover
/// the taskbar and it would be unusable.
///
/// All state is static because the window procedure has to be a static function pointer to
/// remain AOT-compatible - there is no instance to route messages to. Phase 1 is primary
/// monitor only.
/// </summary>
internal static unsafe class TaskbarWindow
{
    private const string ClassName = "WinShellTaskbar";

    // Layout constants are expressed at 96 DPI and scaled through Scale() at use.
    private const int BaseHeight = 40;
    private const int BaseStartWidth = 56;
    private const int BaseClockWidth = 92;
    private const int BaseButtonMaxWidth = 168;
    private const int BaseButtonMinWidth = 44;
    private const int BaseIconSize = 16;
    private const int BasePad = 4;

    private static readonly IntPtr TimerId = new(1);
    private const uint TimerIntervalMs = 1000;

    // Hover sentinels; >= 0 means "task button at this index".
    private const int HoverNone = -1;
    private const int HoverStart = -2;
    private const int HoverIndicator = -3;

    private static Indicator? _hoverIndicator;

    private static IntPtr _hwnd;
    private static uint _dpi = 96;
    private static uint _appBarMessage;
    private static uint _refreshMessage;
    private static bool _appBarRegistered;

    private static int _hover = HoverNone;
    private static bool _mouseTracked;
    private static string _clockTime = string.Empty;
    private static string _clockDate = string.Empty;
    private static int _tickCount;

    // Cached GDI resources. Recreated on DPI change; released on shutdown.
    private static IntPtr _fontUi;
    private static IntPtr _fontClock;
    private static IntPtr _fontClockSmall;
    private static IntPtr _fontGlyph;
    private static IntPtr _brushBg;
    private static IntPtr _brushHover;
    private static IntPtr _brushActive;
    private static IntPtr _brushBorder;
    private static IntPtr _brushAccent;

    // Back buffer, sized to the client area. Kept between paints so a repaint does not churn
    // two GDI allocations every time the clock ticks or a button highlights.
    private static IntPtr _memDc;
    private static IntPtr _memBitmap;
    private static IntPtr _memOldBitmap;
    private static int _memWidth;
    private static int _memHeight;

    private static uint ColorBg => Win32.Rgb(32, 32, 34);
    private static uint ColorBorder => Win32.Rgb(78, 78, 86);
    private static uint ColorHover => Win32.Rgb(58, 58, 64);
    private static uint ColorActive => Win32.Rgb(80, 80, 88);
    private static uint ColorText => Win32.Rgb(230, 230, 234);
    private static uint ColorTextDim => Win32.Rgb(176, 176, 184);
    private static uint ColorAccent => Win32.Rgb(0, 120, 215);

    public static int Height => Scale(BaseHeight);

    private static int Scale(int value) => (int)(value * _dpi / 96.0);

    public static IntPtr Handle => _hwnd;

    public static bool Create()
    {
        IntPtr instance = Win32.GetModuleHandle(null);
        IntPtr classNamePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0,
            lpfnWndProc = &WndProc,
            hInstance = instance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = IntPtr.Zero, // fully self-painted; see WM_ERASEBKGND
            lpszClassName = classNamePtr,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            return false;

        _refreshMessage = Win32.RegisterWindowMessage("WinShell.RefreshWindowList");
        _appBarMessage = Win32.RegisterWindowMessage("WinShell.AppBarCallback");

        // WS_EX_NOACTIVATE keeps clicks on the bar from stealing focus from the window the
        // user is actually working in - without it, clicking a task button would deactivate
        // the very window being switched to.
        _hwnd = Win32.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            ClassName, "WinShell", WS_POPUP | WS_CLIPCHILDREN,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return false;

        _dpi = Win32.GetDpiForWindow(_hwnd);
        if (_dpi == 0)
            _dpi = 96;

        CreateResources();
        RegisterAppBar();
        Dock();

        Win32.ShowWindow(_hwnd, SW_SHOWNA);
        Win32.SetTimer(_hwnd, TimerId, TimerIntervalMs, IntPtr.Zero);
        UpdateClock();

        SystemIndicators.Initialize();
        WindowList.Initialize(_hwnd, _refreshMessage);
        return true;
    }

    public static void Destroy()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        Win32.KillTimer(_hwnd, TimerId);
        SystemIndicators.Shutdown();
        WindowList.Shutdown();
        UnregisterAppBar();
        ReleaseResources();
        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    // ---- AppBar -----------------------------------------------------------------

    private static void RegisterAppBar()
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _hwnd,
            uCallbackMessage = _appBarMessage,
        };

        Win32.SHAppBarMessage(ABM_NEW, ref data);
        _appBarRegistered = true;
    }

    private static void UnregisterAppBar()
    {
        if (!_appBarRegistered)
            return;

        var data = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = _hwnd };
        Win32.SHAppBarMessage(ABM_REMOVE, ref data);
        _appBarRegistered = false;
    }

    /// <summary>
    /// Claims a strip along the bottom edge and moves the window into it. ABM_QUERYPOS lets
    /// the shell push our proposed rectangle out of the way of other appbars before we commit
    /// it with ABM_SETPOS.
    /// </summary>
    private static void Dock()
    {
        int screenWidth = Win32.GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = Win32.GetSystemMetrics(SM_CYSCREEN);
        int height = Height;

        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _hwnd,
            uEdge = ABE_BOTTOM,
            rc = new RECT(0, screenHeight - height, screenWidth, screenHeight),
        };

        // When the native bar is hidden its appbar reservation is still registered, so
        // honouring QUERYPOS would push us above a strip nothing is drawing in and leave a
        // gap along the bottom of the screen.
        if (!NativeTaskbar.Suppressing)
        {
            Win32.SHAppBarMessage(ABM_QUERYPOS, ref data);

            // QUERYPOS only adjusts the edge it owns, so re-derive the opposite edge to keep
            // the bar exactly `height` tall.
            data.rc.Top = data.rc.Bottom - height;
        }

        RECT target = data.rc;
        Win32.SHAppBarMessage(ABM_SETPOS, ref data);

        // SETPOS rewrites the rect in place and will collapse it to nothing when it disagrees
        // with another appbar's claim, so the result is only used when it is actually usable.
        RECT final = NativeTaskbar.Suppressing || data.rc.Height <= 0 ? target : data.rc;

        Win32.SetWindowPos(
            _hwnd, HWND_TOPMOST,
            final.Left, final.Top, final.Width, final.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ---- Window procedure ---------------------------------------------------------

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // Registered messages are not compile-time constants, so they cannot appear in
            // the switch below.
            if (msg == _refreshMessage)
            {
                WindowList.ClearPending();
                WindowList.Refresh();
                Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            if (msg == _appBarMessage)
            {
                if ((int)wParam is ABN_POSCHANGED or ABN_FULLSCREENAPP)
                    Dock();

                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_ERASEBKGND:
                    // Every pixel is painted in WM_PAINT from the back buffer. Letting the
                    // system erase first would just add a visible flash.
                    return new IntPtr(1);

                case WM_PAINT:
                    OnPaint(hwnd);
                    return IntPtr.Zero;

                case WM_TIMER:
                    OnTimer(hwnd);
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    OnMouseMove(hwnd, Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_MOUSELEAVE:
                    _mouseTracked = false;
                    SetHover(hwnd, HoverNone);
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    OnClick(Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    OnRightClick(Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_MOUSEWHEEL:
                    OnWheel(hwnd, Win32.HiWord(wParam), Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_DPICHANGED:
                    _dpi = (uint)Win32.LoWord(wParam);
                    if (_dpi == 0)
                        _dpi = 96;
                    CreateResources();
                    Dock();
                    Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;

                case WM_DISPLAYCHANGE:
                case WM_SETTINGCHANGE:
                    Dock();
                    Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;

                case WM_QUERYENDSESSION:
                    // Put the real taskbar back before logoff/shutdown so the next session
                    // never starts with a hidden shell.
                    NativeTaskbar.Restore();
                    return new IntPtr(1);

                case WM_ENDSESSION:
                    NativeTaskbar.Restore();
                    return IntPtr.Zero;

                case WM_CLOSE:
                    Win32.DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch
        {
            // A managed exception must never unwind into the OS message dispatcher; fall
            // through to DefWindowProc instead of tearing the process down.
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OnTimer(IntPtr hwnd)
    {
        _tickCount++;

        // Repaint only when the displayed minute actually changes - a taskbar that
        // invalidates itself once a second is a taskbar that shows up in Task Manager.
        if (UpdateClock())
            Win32.InvalidateRect(hwnd, IntPtr.Zero, false);

        // Explorer re-shows its taskbar after various shell events. Re-hiding every 5 s is
        // far cheaper than watching for every event that could cause it.
        if (_tickCount % 5 == 0)
            NativeTaskbar.Reapply();

        if (TrayHost.Active && TrayHost.PruneDead())
            Win32.InvalidateRect(hwnd, IntPtr.Zero, false);

        // Volume can change from anywhere (media keys, other apps), so the indicators are
        // polled rather than pushed. Repaint only when a glyph actually changed.
        if (_tickCount % 2 == 0 && SystemIndicators.Refresh())
            Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static bool UpdateClock()
    {
        DateTime now = DateTime.Now;
        string time = now.ToString("h:mm tt");
        string date = now.ToString("M/d/yyyy");

        if (time == _clockTime && date == _clockDate)
            return false;

        _clockTime = time;
        _clockDate = date;
        return true;
    }

    // ---- Layout -------------------------------------------------------------------

    /// <summary>
    /// Assigns each task button its rectangle and returns the Start and clock rects. Layout
    /// runs on both paint and hit-test so the two can never disagree about where a button is.
    /// </summary>
    private static void Layout(int width, int height, out RECT startRect, out RECT clockRect)
    {
        startRect = new RECT(0, 0, Scale(BaseStartWidth), height);
        clockRect = new RECT(width - Scale(BaseClockWidth), 0, width, height);

        int pad = Scale(BasePad);
        int trayRight = clockRect.Left - pad;
        int trayIcon = Scale(BaseIconSize);
        int trayStep = trayIcon + Scale(8);
        int trayCount = TrayHost.Icons.Count;
        int trayLeft = trayRight - (trayCount * trayStep);

        for (int i = 0; i < trayCount; i++)
        {
            int x = trayLeft + (i * trayStep);
            int y = (height - trayIcon) / 2;
            TrayHost.Icons[i].Bounds = new RECT(x, y, x + trayIcon, y + trayIcon);
        }

        int indicatorStep = Scale(24);
        int indicatorRight = trayLeft - (trayCount > 0 ? pad : 0);
        int visibleIndicators = 0;

        foreach (Indicator indicator in SystemIndicators.Items)
        {
            if (indicator.Visible)
                visibleIndicators++;
        }

        int indicatorLeft = indicatorRight - (visibleIndicators * indicatorStep);
        int slot = 0;

        foreach (Indicator indicator in SystemIndicators.Items)
        {
            if (!indicator.Visible)
            {
                indicator.Bounds = default;
                continue;
            }

            int x = indicatorLeft + (slot * indicatorStep);
            indicator.Bounds = new RECT(x, 0, x + indicatorStep, height);
            slot++;
        }

        int left = startRect.Right + pad;
        int available = (visibleIndicators > 0 ? indicatorLeft - pad : trayLeft - pad) - left;
        int count = WindowList.Items.Count;

        if (count == 0 || available <= 0)
            return;

        int buttonWidth = Math.Clamp(available / count, Scale(BaseButtonMinWidth), Scale(BaseButtonMaxWidth));

        int taskRight = visibleIndicators > 0 ? indicatorLeft - pad
            : trayCount > 0 ? trayLeft - pad
            : clockRect.Left;

        for (int i = 0; i < count; i++)
        {
            int x = left + (i * buttonWidth);
            if (x >= taskRight)
            {
                // Ran out of room - collapse the remainder to nothing so hit-testing skips
                // buttons that are not drawn.
                WindowList.Items[i].Bounds = default;
                continue;
            }

            int right = Math.Min(x + buttonWidth - pad, taskRight - pad);
            WindowList.Items[i].Bounds = new RECT(x, pad, right, height - pad);
        }
    }

    // ---- Painting -------------------------------------------------------------------

    private static void OnPaint(IntPtr hwnd)
    {
        IntPtr hdc = Win32.BeginPaint(hwnd, out PAINTSTRUCT ps);
        Win32.GetClientRect(hwnd, out RECT client);

        int width = client.Width;
        int height = client.Height;

        if (width > 0 && height > 0)
        {
            EnsureBackBuffer(hdc, width, height);
            Paint(_memDc, width, height);
            Win32.BitBlt(hdc, 0, 0, width, height, _memDc, 0, 0, SRCCOPY);
        }

        Win32.EndPaint(hwnd, ref ps);
    }

    private static void Paint(IntPtr dc, int width, int height)
    {
        Win32.SetBkMode(dc, TRANSPARENT);

        var full = new RECT(0, 0, width, height);
        Win32.FillRect(dc, ref full, _brushBg);

        // Single-pixel top highlight, the cheap trick that keeps a flat bar from looking
        // like a hole in the screen.
        var topLine = new RECT(0, 0, width, 1);
        Win32.FillRect(dc, ref topLine, _brushBorder);

        Layout(width, height, out RECT startRect, out RECT clockRect);

        PaintStartButton(dc, startRect);
        PaintTasks(dc);
        PaintIndicators(dc);
        PaintTray(dc);
        PaintClock(dc, clockRect);
    }

    private static void PaintIndicators(IntPtr dc)
    {
        IntPtr previousFont = Win32.SelectObject(dc, _fontGlyph);

        foreach (Indicator indicator in SystemIndicators.Items)
        {
            if (!indicator.Visible || indicator.Bounds.Width <= 0 || indicator.Glyph.Length == 0)
                continue;

            RECT bounds = indicator.Bounds;

            if (_hover == HoverIndicator && _hoverIndicator == indicator)
                Win32.FillRect(dc, ref bounds, _brushHover);

            DrawLabel(dc, indicator.Glyph, ref bounds,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorText);
        }

        Win32.SelectObject(dc, previousFont);
    }

    private static void PaintTray(IntPtr dc)
    {
        int size = Scale(BaseIconSize);

        foreach (TrayIcon icon in TrayHost.Icons)
        {
            if (icon.Icon == IntPtr.Zero || icon.Bounds.Width <= 0)
                continue;

            Win32.DrawIconEx(dc, icon.Bounds.Left, icon.Bounds.Top, icon.Icon, size, size, 0, IntPtr.Zero, DI_NORMAL);
        }
    }

    public static void Redock()
    {
        if (_hwnd != IntPtr.Zero)
            Dock();
    }

    public static void Invalidate()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private static void PaintStartButton(IntPtr dc, RECT rect)
    {
        if (_hover == HoverStart)
            Win32.FillRect(dc, ref rect, _brushHover);

        // A 2x2 grid of squares standing in for the Windows mark - drawable with four
        // FillRects and no image asset to ship.
        int cell = Scale(7);
        int gap = Scale(2);
        int totalWidth = (cell * 2) + gap;
        int x = rect.Left + ((rect.Width - totalWidth) / 2);
        int y = rect.Top + ((rect.Height - totalWidth) / 2);

        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                var square = new RECT(
                    x + (col * (cell + gap)),
                    y + (row * (cell + gap)),
                    x + (col * (cell + gap)) + cell,
                    y + (row * (cell + gap)) + cell);

                Win32.FillRect(dc, ref square, _brushAccent);
            }
        }
    }

    private static void PaintTasks(IntPtr dc)
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        IntPtr previousFont = Win32.SelectObject(dc, _fontUi);

        int iconSize = Scale(BaseIconSize);
        int pad = Scale(BasePad);

        for (int i = 0; i < WindowList.Items.Count; i++)
        {
            TaskWindow item = WindowList.Items[i];
            RECT bounds = item.Bounds;

            if (bounds.Width <= 0)
                continue;

            bool isForeground = item.Handle == foreground;

            if (isForeground)
            {
                Win32.FillRect(dc, ref bounds, _brushActive);
                var underline = new RECT(bounds.Left, bounds.Bottom - Scale(2), bounds.Right, bounds.Bottom);
                Win32.FillRect(dc, ref underline, _brushAccent);
            }
            else if (_hover == i)
            {
                Win32.FillRect(dc, ref bounds, _brushHover);
            }

            int x = bounds.Left + pad + Scale(2);

            if (item.Icon != IntPtr.Zero)
            {
                int iconY = bounds.Top + ((bounds.Height - iconSize) / 2);
                Win32.DrawIconEx(dc, x, iconY, item.Icon, iconSize, iconSize, 0, IntPtr.Zero, DI_NORMAL);
                x += iconSize + Scale(6);
            }

            var textRect = new RECT(x, bounds.Top, bounds.Right - pad, bounds.Bottom);
            if (textRect.Width > Scale(8))
            {
                DrawLabel(
                    dc, item.Title, ref textRect,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX,
                    item.IsMinimized ? ColorTextDim : ColorText);
            }
        }

        Win32.SelectObject(dc, previousFont);
    }

    private static void PaintClock(IntPtr dc, RECT rect)
    {
        int half = rect.Height / 2;

        IntPtr previousFont = Win32.SelectObject(dc, _fontClock);
        var timeRect = new RECT(rect.Left, rect.Top + Scale(3), rect.Right - Scale(8), rect.Top + half + Scale(2));
        DrawLabel(dc, _clockTime, ref timeRect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorText);

        Win32.SelectObject(dc, _fontClockSmall);
        var dateRect = new RECT(rect.Left, rect.Top + half - Scale(1), rect.Right - Scale(8), rect.Bottom - Scale(3));
        DrawLabel(dc, _clockDate, ref dateRect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorTextDim);

        Win32.SelectObject(dc, previousFont);
    }

    private static void DrawLabel(IntPtr dc, string text, ref RECT rect, uint format, uint color)
    {
        if (text.Length == 0)
            return;

        Win32.SetTextColor(dc, color);
        fixed (char* p = text)
        {
            Win32.DrawText(dc, p, text.Length, ref rect, format);
        }
    }

    private static void EnsureBackBuffer(IntPtr hdc, int width, int height)
    {
        if (_memDc != IntPtr.Zero && width == _memWidth && height == _memHeight)
            return;

        ReleaseBackBuffer();

        _memDc = Win32.CreateCompatibleDC(hdc);
        _memBitmap = Win32.CreateCompatibleBitmap(hdc, width, height);
        _memOldBitmap = Win32.SelectObject(_memDc, _memBitmap);
        _memWidth = width;
        _memHeight = height;
    }

    private static void ReleaseBackBuffer()
    {
        if (_memDc == IntPtr.Zero)
            return;

        // The original bitmap has to go back into the DC before the one we created can be
        // deleted, or GDI keeps it alive and the handle leaks.
        Win32.SelectObject(_memDc, _memOldBitmap);
        Win32.DeleteObject(_memBitmap);
        Win32.DeleteDC(_memDc);

        _memDc = IntPtr.Zero;
        _memBitmap = IntPtr.Zero;
        _memOldBitmap = IntPtr.Zero;
        _memWidth = 0;
        _memHeight = 0;
    }

    // ---- Mouse ------------------------------------------------------------------------

    private static void OnMouseMove(IntPtr hwnd, int x, int y)
    {
        if (!_mouseTracked)
        {
            // WM_MOUSELEAVE is not sent unless it is explicitly requested, and without it a
            // hover highlight would stay lit after the pointer left the bar.
            var track = new TRACKMOUSEEVENT
            {
                cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                dwFlags = TME_LEAVE,
                hwndTrack = hwnd,
            };

            Win32.TrackMouseEvent(ref track);
            _mouseTracked = true;
        }

        SetHover(hwnd, HitTest(x, y));
    }

    private static void SetHover(IntPtr hwnd, int target)
    {
        if (_hover == target)
            return;

        _hover = target;
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static int HitTest(int x, int y)
    {
        Win32.GetClientRect(_hwnd, out RECT client);
        Layout(client.Width, client.Height, out RECT startRect, out _);

        if (startRect.Contains(x, y))
            return HoverStart;

        foreach (Indicator indicator in SystemIndicators.Items)
        {
            if (indicator.Visible && indicator.Bounds.Width > 0 && indicator.Bounds.Contains(x, y))
            {
                _hoverIndicator = indicator;
                return HoverIndicator;
            }
        }

        _hoverIndicator = null;

        for (int i = 0; i < WindowList.Items.Count; i++)
        {
            if (WindowList.Items[i].Bounds.Contains(x, y))
                return i;
        }

        return HoverNone;
    }

    private static void OnClick(int x, int y)
    {
        foreach (TrayIcon icon in TrayHost.Icons)
        {
            if (icon.Bounds.Width > 0 && icon.Bounds.Contains(x, y))
            {
                TrayHost.Click(icon, WM_LBUTTONUP);
                return;
            }
        }

        int target = HitTest(x, y);

        if (target == HoverStart)
        {
            StartMenu.Toggle();
            return;
        }

        if (target == HoverIndicator && _hoverIndicator != null)
        {
            if (_hoverIndicator.Kind == IndicatorKind.Volume)
            {
                Win32.GetWindowRect(_hwnd, out RECT window);
                RECT local = _hoverIndicator.Bounds;
                var anchor = new RECT(
                    window.Left + local.Left, window.Top + local.Top,
                    window.Left + local.Right, window.Top + local.Bottom);

                VolumeFlyout.Toggle(anchor);
            }
            else
            {
                SystemIndicators.Open(_hoverIndicator.Kind);
            }

            return;
        }

        HandleTaskClick(target);
    }

    private static void OnRightClick(int x, int y)
    {
        foreach (TrayIcon icon in TrayHost.Icons)
        {
            if (icon.Bounds.Width > 0 && icon.Bounds.Contains(x, y))
            {
                TrayHost.Click(icon, WM_RBUTTONUP);
                return;
            }
        }

        if (HitTest(x, y) == HoverIndicator && _hoverIndicator != null)
            SystemIndicators.Open(_hoverIndicator.Kind);
    }

    private static void OnWheel(IntPtr hwnd, int delta, int screenX, int screenY)
    {
        Win32.GetWindowRect(hwnd, out RECT window);
        int x = screenX - window.Left;
        int y = screenY - window.Top;

        if (HitTest(x, y) != HoverIndicator || _hoverIndicator?.Kind != IndicatorKind.Volume)
            return;

        SystemIndicators.AdjustVolume(delta / 120);
        SystemIndicators.Refresh();
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void HandleTaskClick(int target)
    {

        if (target >= 0 && target < WindowList.Items.Count)
        {
            WindowList.Activate(WindowList.Items[target]);
            Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    // ---- GDI resources ------------------------------------------------------------------

    private static void CreateResources()
    {
        ReleaseResources();

        _fontUi = CreateUiFont(Scale(12), 400);
        _fontClock = CreateUiFont(Scale(12), 400);
        _fontClockSmall = CreateUiFont(Scale(10), 400);
        _fontGlyph = CreateGlyphFont(Scale(14));

        _brushBg = Win32.CreateSolidBrush(ColorBg);
        _brushHover = Win32.CreateSolidBrush(ColorHover);
        _brushActive = Win32.CreateSolidBrush(ColorActive);
        _brushBorder = Win32.CreateSolidBrush(ColorBorder);
        _brushAccent = Win32.CreateSolidBrush(ColorAccent);
    }

    private static IntPtr CreateGlyphFont(int pixelHeight)
    {
        const uint DEFAULT_CHARSET = 1;
        const uint CLEARTYPE_QUALITY = 5;

        return Win32.CreateFont(
            -pixelHeight, 0, 0, 0, 400,
            0, 0, 0, DEFAULT_CHARSET,
            0, 0, CLEARTYPE_QUALITY, 0,
            "Segoe MDL2 Assets");
    }

    private static IntPtr CreateUiFont(int pixelHeight, int weight)
    {
        const uint DEFAULT_CHARSET = 1;
        const uint CLEARTYPE_QUALITY = 5;

        return Win32.CreateFont(
            -pixelHeight, 0, 0, 0, weight,
            0, 0, 0, DEFAULT_CHARSET,
            0, 0, CLEARTYPE_QUALITY, 0,
            "Segoe UI");
    }

    private static void ReleaseResources()
    {
        IntPtr[] handles =
        [
            _fontUi, _fontClock, _fontClockSmall, _fontGlyph,
            _brushBg, _brushHover, _brushActive, _brushBorder, _brushAccent,
        ];

        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
                Win32.DeleteObject(handle);
        }

        _fontUi = _fontClock = _fontClockSmall = _fontGlyph = IntPtr.Zero;
        _brushBg = _brushHover = _brushActive = _brushBorder = _brushAccent = IntPtr.Zero;

        ReleaseBackBuffer();
    }
}
