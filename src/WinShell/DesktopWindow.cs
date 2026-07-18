using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// The desktop: a full-screen window pinned to the bottom of the z-order that paints the
/// wallpaper and the icons that live in the two Desktop folders.
///
/// This exists because the desktop was never a service we could ask for - it is a window
/// Explorer creates (Progman, with SHELLDLL_DefView inside it) and it only creates it when it
/// starts and finds no shell already running. Once WinShell owns the Winlogon Shell value,
/// Explorer never builds one, which is why replacing the shell left a bare background with no
/// wallpaper, no icons and no right-click. Everything that surface used to provide has to be
/// provided here instead.
///
/// Staying at the bottom is enforced on every WM_WINDOWPOSCHANGING rather than set once: the
/// window is activatable (it needs the keyboard for F2 and Delete), and without the pin,
/// clicking it would raise it over the windows the user is actually working in.
///
/// All state is static for the same reason as TaskbarWindow - the window procedure has to be
/// a static function pointer to stay AOT-clean, so there is no instance to dispatch to.
/// </summary>
internal static unsafe class DesktopWindow
{
    private const string ClassName = "WinShellDesktop";

    // Layout at 96 DPI, scaled through Scale() at use. The cell is Explorer's shape: a 48px
    // icon with room for two lines of label underneath.
    private const int BaseCellWidth = 88;
    private const int BaseCellHeight = 104;
    private const int BaseIconSize = 48;
    private const int BaseMargin = 12;
    private const int BaseIconTop = 10;
    private const int BaseLineHeight = 15;
    private const int BaseLabelPad = 4;

    // How far the mouse must travel before a click becomes a drag. Without a threshold a
    // slightly shaky click would move an icon every time.
    private const int BaseDragThreshold = 5;

    private static IntPtr _hwnd;
    private static uint _dpi = 96;
    private static uint _refreshMessage;

    private static IntPtr _fontLabel;
    private static IntPtr _blendDc;
    private static IntPtr _blendBitmap;
    private static IntPtr _blendOldBitmap;

    private static IntPtr _memDc;
    private static IntPtr _memBitmap;
    private static IntPtr _memOldBitmap;
    private static int _memWidth;
    private static int _memHeight;

    private static FileSystemWatcher? _userWatcher;
    private static FileSystemWatcher? _publicWatcher;

    // Mouse state. _pressed is the item the button went down on, which is not necessarily the
    // item that ends up selected - that is decided on mouse up if no drag happened.
    private static DesktopItem? _pressed;
    private static bool _dragging;
    private static bool _banding;
    private static int _anchorX;
    private static int _anchorY;
    private static int _cursorX;
    private static int _cursorY;

    private static uint ColorLabel => Win32.Rgb(255, 255, 255);
    private static uint ColorLabelShadow => Win32.Rgb(0, 0, 0);
    private static uint ColorSelection => Win32.Rgb(0, 120, 215);

    public static IntPtr Handle => _hwnd;

    private static int Scale(int value) => (int)(value * _dpi / 96.0);

    private static int CellWidth => Scale(BaseCellWidth);
    private static int CellHeight => Scale(BaseCellHeight);
    private static int Margin => Scale(BaseMargin);

    // ---- Lifetime -----------------------------------------------------------------------

    public static bool Create()
    {
        IntPtr instance = Win32.GetModuleHandle(null);
        IntPtr classNamePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),

            // CS_DBLCLKS is what makes double clicks arrive as WM_LBUTTONDBLCLK at all.
            // Without it nothing on the desktop would ever open.
            style = CS_DBLCLKS,
            lpfnWndProc = &WndProc,
            hInstance = instance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = classNamePtr,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            return false;

        _refreshMessage = Win32.RegisterWindowMessage("WinShell.RefreshDesktop");

        int width = Win32.GetSystemMetrics(SM_CXSCREEN);
        int height = Win32.GetSystemMetrics(SM_CYSCREEN);

        _hwnd = Win32.CreateWindowEx(
            WS_EX_TOOLWINDOW, ClassName, "Desktop", WS_POPUP,
            0, 0, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return false;

        _dpi = Win32.GetDpiForWindow(_hwnd);

        if (_dpi == 0)
            _dpi = 96;

        CreateResources();
        Wallpaper.Initialize();

        DesktopIcons.Refresh();
        Relayout();

        Win32.DragAcceptFiles(_hwnd, true);
        StartWatching();

        // SHOWNOACTIVATE, then an explicit sink to the bottom: showing it normally would put
        // the desktop in front of every window that is already open.
        Win32.ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SinkToBottom();

        return true;
    }

    public static void Destroy()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        StopWatching();
        DesktopIcons.SaveLayout();
        DesktopIcons.Release();
        Wallpaper.Shutdown();
        ReleaseResources();

        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static void SinkToBottom() =>
        Win32.SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // ---- Watching the desktop folders ------------------------------------------------------

    /// <summary>
    /// Files appear on the desktop without anyone telling us, so both folders are watched and
    /// a change posts a refresh onto the message loop. The post is what makes this safe: the
    /// watcher callbacks arrive on thread-pool threads and must not touch the icon list, which
    /// belongs to the UI thread.
    /// </summary>
    private static void StartWatching()
    {
        _userWatcher = Watch(DesktopIcons.UserDesktop);
        _publicWatcher = Watch(DesktopIcons.PublicDesktop);
    }

    private static FileSystemWatcher? Watch(string path)
    {
        if (path.Length == 0 || !Directory.Exists(path))
            return null;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
            };

            watcher.Created += OnFolderChanged;
            watcher.Deleted += OnFolderChanged;
            watcher.Renamed += OnFolderChanged;
            watcher.Changed += OnFolderChanged;
            watcher.EnableRaisingEvents = true;

            return watcher;
        }
        catch
        {
            return null;
        }
    }

    private static void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
            Win32.PostMessage(_hwnd, _refreshMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private static void StopWatching()
    {
        _userWatcher?.Dispose();
        _publicWatcher?.Dispose();
        _userWatcher = null;
        _publicWatcher = null;
    }

    // ---- Resources ---------------------------------------------------------------------------

    private static void CreateResources()
    {
        _fontLabel = Win32.CreateFont(
            -Scale(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

        // A one-pixel scratch surface that every alpha blend is stretched from. Selection
        // washes and the rubber band are flat colour, so one pixel is all the source needs to
        // be, and caching it keeps repaints from churning two GDI objects per rectangle.
        IntPtr screen = Win32.CreateCompatibleDC(IntPtr.Zero);
        _blendDc = Win32.CreateCompatibleDC(screen);
        _blendBitmap = Win32.CreateCompatibleBitmap(screen, 1, 1);
        _blendOldBitmap = Win32.SelectObject(_blendDc, _blendBitmap);
        Win32.DeleteDC(screen);
    }

    private static void ReleaseResources()
    {
        if (_fontLabel != IntPtr.Zero)
            Win32.DeleteObject(_fontLabel);

        if (_blendDc != IntPtr.Zero)
        {
            Win32.SelectObject(_blendDc, _blendOldBitmap);
            Win32.DeleteObject(_blendBitmap);
            Win32.DeleteDC(_blendDc);
        }

        ReleaseBackBuffer();

        _fontLabel = IntPtr.Zero;
        _blendDc = IntPtr.Zero;
        _blendBitmap = IntPtr.Zero;
    }

    private static void ReleaseBackBuffer()
    {
        if (_memDc == IntPtr.Zero)
            return;

        Win32.SelectObject(_memDc, _memOldBitmap);
        Win32.DeleteObject(_memBitmap);
        Win32.DeleteDC(_memDc);

        _memDc = IntPtr.Zero;
        _memBitmap = IntPtr.Zero;
        _memWidth = 0;
        _memHeight = 0;
    }

    // ---- Layout ---------------------------------------------------------------------------------

    /// <summary>
    /// Icons are laid out inside the work area, not the screen: the taskbar reserves a strip
    /// along the bottom as an appbar and an icon underneath it would be unreachable.
    /// </summary>
    private static void Relayout()
    {
        Win32.GetClientRect(_hwnd, out RECT client);

        var work = default(RECT);
        int usableHeight = client.Height;

        if (Win32.SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0) && work.Height > 0)
            usableHeight = Math.Min(client.Height, work.Height);

        DesktopIcons.Layout(client.Width, usableHeight, CellWidth, CellHeight, Margin, Margin);

        // Label rectangles are derived here rather than while painting. They are layout, not
        // decoration: F2 places the rename box over one, and deriving it in the paint path
        // meant renaming an icon that had not been painted yet used an empty rectangle.
        foreach (DesktopItem item in DesktopIcons.All)
            item.LabelBounds = LabelRectFor(item.Bounds);
    }

    private static RECT LabelRectFor(RECT bounds)
    {
        int pad = Scale(BaseLabelPad);
        int top = bounds.Top + Scale(BaseIconTop) + Scale(BaseIconSize) + pad;

        return new RECT(bounds.Left + pad, top, bounds.Right - pad, top + Scale(BaseLineHeight) * 2);
    }

    public static void Refresh()
    {
        DesktopIcons.SaveLayout();
        DesktopIcons.Refresh();
        Relayout();
        Invalidate();
    }

    public static void Invalidate()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>
    /// Drives a click through the same path a real one takes. Exists so selection and hit
    /// testing can be tested without posting messages from another process, where a silent
    /// failure is indistinguishable from a click that landed somewhere unexpected.
    /// </summary>
    public static void SimulateClick(int x, int y)
    {
        OnLeftDown(x, y, control: false);
        OnLeftUp(control: false);
    }

    /// <summary>Starts an in-place rename on the selection, for the same testing reason.</summary>
    public static void SimulateRename() => RenameSelection();

    /// <summary>Exercises the shell context menu plumbing without showing a menu.</summary>
    public static string ProbeContextMenus()
    {
        var paths = new List<string>();

        foreach (DesktopItem item in DesktopIcons.All)
            paths.Add(item.Path);

        return ShellContextMenu.Probe(_hwnd, paths, DesktopIcons.UserDesktop);
    }

    /// <summary>
    /// Reports the resolved geometry and what was laid out where. Written because eyeballing
    /// a screenshot cannot distinguish "correct at 200% DPI" from "scaled by the compositor",
    /// and the two look identical until something is clicked.
    /// </summary>
    public static string Diagnostics()
    {
        Win32.GetClientRect(_hwnd, out RECT client);

        var work = default(RECT);
        Win32.SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0);

        var report = new System.Text.StringBuilder();

        report.AppendLine($"dpi           : {_dpi} (scale {_dpi / 96.0:0.##}x)");
        report.AppendLine($"client        : {client.Width} x {client.Height}");
        report.AppendLine($"work area     : {work.Width} x {work.Height}");
        report.AppendLine($"cell          : {CellWidth} x {CellHeight}, margin {Margin}");
        report.AppendLine($"icon          : {Scale(BaseIconSize)}px");
        report.AppendLine($"rename active : {DesktopRename.Active}");
        report.AppendLine($"items         : {DesktopIcons.All.Count}");

        foreach (DesktopItem item in DesktopIcons.All)
        {
            report.AppendLine(
                $"  [{(item.Selected ? "x" : " ")}] cell({item.Column},{item.Row}) " +
                $"bounds({item.Bounds.Left},{item.Bounds.Top},{item.Bounds.Right},{item.Bounds.Bottom}) {item.Name}");
        }

        return report.ToString();
    }

    // ---- Painting --------------------------------------------------------------------------------

    private static void Paint()
    {
        IntPtr hdc = Win32.BeginPaint(_hwnd, out PAINTSTRUCT ps);
        Win32.GetClientRect(_hwnd, out RECT client);

        IntPtr target = EnsureBackBuffer(hdc, client.Width, client.Height);

        Wallpaper.Paint(target, client.Width, client.Height);

        IntPtr previousFont = Win32.SelectObject(target, _fontLabel);
        Win32.SetBkMode(target, TRANSPARENT);

        foreach (DesktopItem item in DesktopIcons.All)
            PaintItem(target, item);

        if (_banding)
            PaintBand(target);

        Win32.SelectObject(target, previousFont);

        if (target != hdc)
            Win32.BitBlt(hdc, 0, 0, client.Width, client.Height, target, 0, 0, SRCCOPY);

        Win32.EndPaint(_hwnd, ref ps);
    }

    private static IntPtr EnsureBackBuffer(IntPtr hdc, int width, int height)
    {
        if (_memDc != IntPtr.Zero && _memWidth == width && _memHeight == height)
            return _memDc;

        ReleaseBackBuffer();

        _memDc = Win32.CreateCompatibleDC(hdc);

        if (_memDc == IntPtr.Zero)
            return hdc;

        _memBitmap = Win32.CreateCompatibleBitmap(hdc, width, height);

        if (_memBitmap == IntPtr.Zero)
        {
            Win32.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            return hdc;
        }

        _memOldBitmap = Win32.SelectObject(_memDc, _memBitmap);
        _memWidth = width;
        _memHeight = height;

        return _memDc;
    }

    private static void PaintItem(IntPtr hdc, DesktopItem item)
    {
        RECT bounds = item.Bounds;

        // While dragging, the selection is drawn at the cursor offset. The underlying cells
        // are untouched until the drop commits, so an abandoned drag costs nothing.
        if (_dragging && item.Selected)
        {
            int dx = _cursorX - _anchorX;
            int dy = _cursorY - _anchorY;
            bounds = new RECT(bounds.Left + dx, bounds.Top + dy, bounds.Right + dx, bounds.Bottom + dy);
        }

        int iconSize = Scale(BaseIconSize);
        int iconX = bounds.Left + (bounds.Width - iconSize) / 2;
        int iconY = bounds.Top + Scale(BaseIconTop);

        if (item.Selected)
        {
            var wash = new RECT(iconX - Scale(4), iconY - Scale(4), iconX + iconSize + Scale(4), iconY + iconSize + Scale(4));
            BlendRect(hdc, wash, ColorSelection, 90);
        }

        IntPtr icon = DesktopIcons.Icon(item);

        if (icon != IntPtr.Zero)
            Win32.DrawIconEx(hdc, iconX, iconY, icon, iconSize, iconSize, 0, IntPtr.Zero, DI_NORMAL);

        PaintLabel(hdc, item, bounds);
    }

    private static void PaintLabel(IntPtr hdc, DesktopItem item, RECT bounds)
    {
        // Recomputed from the possibly drag-offset bounds rather than read from the item, so
        // a label follows its icon while it is being dragged.
        RECT label = LabelRectFor(bounds);

        uint format = DT_CENTER | DT_WORDBREAK | DT_END_ELLIPSIS | DT_NOPREFIX;

        fixed (char* text = item.Name)
        {
            if (item.Selected)
            {
                BlendRect(hdc, label, ColorSelection, 160);
            }
            else
            {
                // A one-pixel shadow, because white-on-wallpaper is unreadable over a pale
                // image and Explorer solves it the same way.
                var shadow = new RECT(label.Left + 1, label.Top + 1, label.Right + 1, label.Bottom + 1);
                Win32.SetTextColor(hdc, ColorLabelShadow);
                Win32.DrawText(hdc, text, item.Name.Length, ref shadow, format);
            }

            Win32.SetTextColor(hdc, ColorLabel);

            RECT draw = label;
            Win32.DrawText(hdc, text, item.Name.Length, ref draw, format);
        }
    }

    private static void PaintBand(IntPtr hdc)
    {
        RECT band = BandRect();

        BlendRect(hdc, band, ColorSelection, 60);

        // A one-pixel frame, drawn as four fills. Cheaper than selecting a pen and a hollow
        // brush, and it cannot leak either of them.
        IntPtr brush = Win32.CreateSolidBrush(ColorSelection);

        var top = new RECT(band.Left, band.Top, band.Right, band.Top + 1);
        var bottom = new RECT(band.Left, band.Bottom - 1, band.Right, band.Bottom);
        var left = new RECT(band.Left, band.Top, band.Left + 1, band.Bottom);
        var right = new RECT(band.Right - 1, band.Top, band.Right, band.Bottom);

        Win32.FillRect(hdc, ref top, brush);
        Win32.FillRect(hdc, ref bottom, brush);
        Win32.FillRect(hdc, ref left, brush);
        Win32.FillRect(hdc, ref right, brush);

        Win32.DeleteObject(brush);
    }

    private static void BlendRect(IntPtr hdc, RECT rect, uint color, byte alpha)
    {
        if (_blendDc == IntPtr.Zero || rect.Width <= 0 || rect.Height <= 0)
            return;

        var one = new RECT(0, 0, 1, 1);
        IntPtr brush = Win32.CreateSolidBrush(color);
        Win32.FillRect(_blendDc, ref one, brush);
        Win32.DeleteObject(brush);

        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = 0,
        };

        Win32.AlphaBlend(hdc, rect.Left, rect.Top, rect.Width, rect.Height, _blendDc, 0, 0, 1, 1, blend);
    }

    private static RECT BandRect() =>
        new(Math.Min(_anchorX, _cursorX), Math.Min(_anchorY, _cursorY),
            Math.Max(_anchorX, _cursorX), Math.Max(_anchorY, _cursorY));

    // ---- Selection --------------------------------------------------------------------------------

    private static void ClearSelection()
    {
        foreach (DesktopItem item in DesktopIcons.All)
            item.Selected = false;
    }

    public static void SelectAll()
    {
        foreach (DesktopItem item in DesktopIcons.All)
            item.Selected = true;

        Invalidate();
    }

    private static void SelectWithinBand()
    {
        RECT band = BandRect();

        foreach (DesktopItem item in DesktopIcons.All)
        {
            RECT b = item.Bounds;

            // Standard rectangle intersection: touching counts, which matches how Explorer's
            // rubber band feels when it clips the edge of an icon.
            item.Selected = b.Left < band.Right && b.Right > band.Left &&
                            b.Top < band.Bottom && b.Bottom > band.Top;
        }
    }

    private static List<DesktopItem> Selected()
    {
        var result = new List<DesktopItem>();

        foreach (DesktopItem item in DesktopIcons.All)
        {
            if (item.Selected)
                result.Add(item);
        }

        return result;
    }

    // ---- Dragging ------------------------------------------------------------------------------------

    /// <summary>
    /// Commits a drag by shifting every selected icon through the same cell delta, then
    /// resolving anything that landed on an occupied cell. Moving the whole selection by one
    /// delta is what keeps a multi-icon drag in formation.
    /// </summary>
    private static void CommitDrag()
    {
        int dx = _cursorX - _anchorX;
        int dy = _cursorY - _anchorY;

        int columnDelta = (int)Math.Round((double)dx / CellWidth);
        int rowDelta = (int)Math.Round((double)dy / CellHeight);

        if (columnDelta == 0 && rowDelta == 0)
            return;

        List<DesktopItem> moving = Selected();
        var occupied = new HashSet<long>();

        foreach (DesktopItem item in DesktopIcons.All)
        {
            if (!item.Selected)
                occupied.Add(((long)item.Column << 32) | (uint)item.Row);
        }

        foreach (DesktopItem item in moving)
        {
            int column = Math.Max(0, item.Column + columnDelta);
            int row = Math.Max(0, item.Row + rowDelta);

            // Walk downwards then across for the first free cell, which is the same order the
            // initial layout fills in, so a collision resolves somewhere predictable.
            while (occupied.Contains(((long)column << 32) | (uint)row))
            {
                row++;

                if (row > 200)
                {
                    row = 0;
                    column++;
                }
            }

            item.Column = column;
            item.Row = row;
            occupied.Add(((long)column << 32) | (uint)row);
        }

        Relayout();
        DesktopIcons.SaveLayout();
    }

    // ---- Actions -------------------------------------------------------------------------------------

    private static void OpenSelection()
    {
        foreach (DesktopItem item in Selected())
            ShellLaunch.Open(item.Path);
    }

    private static void DeleteSelection()
    {
        List<DesktopItem> selected = Selected();

        if (selected.Count == 0)
            return;

        var paths = new List<string>(selected.Count);

        foreach (DesktopItem item in selected)
            paths.Add(item.Path);

        if (DesktopFileOps.Recycle(_hwnd, paths))
            Refresh();
    }

    private static void RenameSelection()
    {
        List<DesktopItem> selected = Selected();

        if (selected.Count == 1)
            DesktopRename.Begin(_hwnd, selected[0], _fontLabel);
    }

    private static void ShowContextMenu(int screenX, int screenY, DesktopItem? item)
    {
        List<DesktopItem> selected = Selected();

        if (item != null)
        {
            var paths = new List<string>(selected.Count);

            foreach (DesktopItem entry in selected)
                paths.Add(entry.Path);

            ShellContextMenu.ShowForItems(_hwnd, screenX, screenY, paths);
        }
        else
        {
            ShellContextMenu.ShowForBackground(_hwnd, screenX, screenY, DesktopIcons.UserDesktop);
        }
    }

    // ---- Window procedure -----------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // The desktop is the one surface a user can always reach, so an exception escaping
        // here would be especially bad: it would take the whole shell with it.
        try
        {
            return Dispatch(hwnd, msg, wParam, lParam);
        }
        catch
        {
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _refreshMessage && _refreshMessage != 0)
        {
            Refresh();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_ERASEBKGND:
                // Fully self-painted; letting the system erase would flash the wallpaper.
                return new IntPtr(1);

            case WM_PAINT:
                Paint();
                return IntPtr.Zero;

            case WM_WINDOWPOSCHANGING:
            {
                // Re-asserted on every position change rather than set once at creation:
                // activating the window would otherwise lift it above everything else.
                var pos = (WINDOWPOS*)lParam;
                pos->hwndInsertAfter = HWND_BOTTOM;
                pos->flags &= ~SWP_NOZORDER;
                return IntPtr.Zero;
            }

            case WM_LBUTTONDOWN:
                OnLeftDown(Win32.LoWord(lParam), Win32.HiWord(lParam), ((uint)wParam & MK_CONTROL) != 0);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                OnMouseMove(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnLeftUp(((uint)wParam & MK_CONTROL) != 0);
                return IntPtr.Zero;

            case WM_LBUTTONDBLCLK:
                OpenSelection();
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                OnRightDown(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return IntPtr.Zero;

            case WM_KEYDOWN:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;

            case WM_DROPFILES:
                OnDropFiles(wParam);
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                OnDisplayChange();
                return IntPtr.Zero;

            case WM_SETTINGCHANGE:
                // Covers a wallpaper change made in Settings, which arrives as nothing more
                // specific than "something changed".
                Wallpaper.Reload();
                Relayout();
                Invalidate();
                return IntPtr.Zero;

            case WM_INITMENUPOPUP:
            case WM_DRAWITEM:
            case WM_MEASUREITEM:
            case WM_MENUCHAR:
                // Shell extensions populate their submenus lazily, so these have to reach the
                // context menu handler or "Open with" and "New" stay empty.
                if (ShellContextMenu.HandleMenuMessage(msg, wParam, lParam, out IntPtr menuResult))
                    return menuResult;

                break;

            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OnDisplayChange()
    {
        int width = Win32.GetSystemMetrics(SM_CXSCREEN);
        int height = Win32.GetSystemMetrics(SM_CYSCREEN);

        Win32.SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, width, height, SWP_NOACTIVATE);

        _dpi = Win32.GetDpiForWindow(_hwnd);

        if (_dpi == 0)
            _dpi = 96;

        Wallpaper.Reload();
        Relayout();
        Invalidate();
    }

    private static void OnLeftDown(int x, int y, bool control)
    {
        DesktopRename.Commit();

        _anchorX = x;
        _anchorY = y;
        _cursorX = x;
        _cursorY = y;

        _pressed = DesktopIcons.HitTest(x, y);
        _dragging = false;
        _banding = false;

        if (_pressed == null)
        {
            if (!control)
                ClearSelection();

            _banding = true;
            Win32.SetCapture(_hwnd);
            Invalidate();
            return;
        }

        if (control)
        {
            _pressed.Selected = !_pressed.Selected;
        }
        else if (!_pressed.Selected)
        {
            // Clicking an unselected icon selects just it. Clicking one that is already part
            // of a selection leaves the selection alone, so a multi-icon drag can start.
            ClearSelection();
            _pressed.Selected = true;
        }

        Win32.SetCapture(_hwnd);
        Invalidate();
    }

    private static void OnMouseMove(int x, int y)
    {
        _cursorX = x;
        _cursorY = y;

        if (_banding)
        {
            SelectWithinBand();
            Invalidate();
            return;
        }

        if (_pressed == null)
            return;

        if (!_dragging)
        {
            int threshold = Scale(BaseDragThreshold);

            if (Math.Abs(x - _anchorX) < threshold && Math.Abs(y - _anchorY) < threshold)
                return;

            _dragging = true;
        }

        Invalidate();
    }

    private static void OnLeftUp(bool control)
    {
        Win32.ReleaseCapture();

        if (_banding)
        {
            _banding = false;
            Invalidate();
            return;
        }

        if (_dragging)
        {
            CommitDrag();
            _dragging = false;
        }
        else if (_pressed != null && !control)
        {
            // A plain click that never became a drag collapses a multi-selection down to the
            // icon actually clicked - deferred to mouse up precisely so the drag could happen.
            ClearSelection();
            _pressed.Selected = true;
        }

        _pressed = null;
        Invalidate();
    }

    private static void OnRightDown(int x, int y)
    {
        DesktopRename.Commit();

        DesktopItem? item = DesktopIcons.HitTest(x, y);

        if (item != null && !item.Selected)
        {
            ClearSelection();
            item.Selected = true;
        }
        else if (item == null)
        {
            ClearSelection();
        }

        Invalidate();

        // Repaint before the menu blocks: TrackPopupMenuEx runs its own message loop, so a
        // pending WM_PAINT would not be serviced until the menu closes.
        Win32.SetForegroundWindow(_hwnd);

        Win32.GetCursorPos(out POINT cursor);
        ShowContextMenu(cursor.X, cursor.Y, item);
    }

    private static void OnKeyDown(int key)
    {
        bool control = (Win32.GetKeyState(VK_CONTROL) & 0x8000) != 0;

        switch (key)
        {
            case VK_F5:
                Refresh();
                break;

            case VK_F2:
                RenameSelection();
                break;

            case VK_DELETE:
                DeleteSelection();
                break;

            case VK_RETURN:
                OpenSelection();
                break;

            case VK_A when control:
                SelectAll();
                break;
        }
    }

    private static void OnDropFiles(IntPtr drop)
    {
        try
        {
            // Passing 0xFFFFFFFF as the index asks for the count rather than a path.
            uint count = Win32.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);

            const int max = 260;
            char* buffer = stackalloc char[max];

            for (uint i = 0; i < count; i++)
            {
                uint length = Win32.DragQueryFile(drop, i, buffer, max);

                if (length > 0)
                    paths.Add(new string(buffer, 0, (int)length));
            }

            if (paths.Count > 0 && DesktopFileOps.DropOnto(_hwnd, paths, DesktopIcons.UserDesktop))
                Refresh();
        }
        finally
        {
            Win32.DragFinish(drop);
        }
    }
}
