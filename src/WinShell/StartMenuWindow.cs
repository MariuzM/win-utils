using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal static unsafe class StartMenuWindow
{
    private const string ClassName = "WinShellStartMenu";

    private const int BaseWidth = 460;
    private const int BaseHeight = 520;
    private const int BasePad = 10;
    private const int BaseLeftWidth = 264;
    private const int BaseRowHeight = 26;
    private const int BaseIconSize = 16;
    private const int BaseSearchHeight = 28;

    private const int HoverNone = -1;

    private enum ActionKind
    {
        Open,
        Settings,
        Lock,
        SignOut,
        Restart,
        Shutdown,
    }

    private sealed class SideItem
    {
        public string Label = string.Empty;
        public string Target = string.Empty;
        public ActionKind Kind;
        public bool IsPower;
        public RECT Bounds;
    }

    private enum ResultKind
    {
        App,
        Setting,
        File,
    }

    private sealed class MenuResult
    {
        public ResultKind Kind;
        public string Label = string.Empty;
        public string Detail = string.Empty;
        public string Target = string.Empty;
        public AppEntry? App;
        public bool IsDirectory;
        public RECT Bounds;
    }

    private static readonly SideItem[] Side =
    [
        new() { Label = "This PC", Target = "shell:MyComputerFolder" },
        new() { Label = "Documents", Target = "shell:Personal" },
        new() { Label = "Downloads", Target = "shell:Downloads" },
        new() { Label = "Pictures", Target = "shell:My Pictures" },
        new() { Label = "Music", Target = "shell:My Music" },
        new() { Label = "Settings", Target = "ms-settings:" },
        new() { Label = "Control Panel", Target = "shell:ControlPanelFolder" },
        new() { Label = "WinShell Settings", Kind = ActionKind.Settings },
        new() { Label = "Lock", Kind = ActionKind.Lock, IsPower = true },
        new() { Label = "Sign out", Kind = ActionKind.SignOut, IsPower = true },
        new() { Label = "Restart", Kind = ActionKind.Restart, IsPower = true },
        new() { Label = "Shut down", Kind = ActionKind.Shutdown, IsPower = true },
    ];

    private static IntPtr _hwnd;
    private static uint _dpi = 96;
    private static bool _visible;

    private static string _query = string.Empty;
    private static readonly List<MenuResult> Filtered = new();
    private static readonly List<(string Path, string Name)> FileHits = new();

    private static readonly IntPtr SearchTimerId = new(7);
    private const uint SearchDebounceMs = 250;
    private static bool _searchPending;

    private static int _scroll;
    private static int _selected = HoverNone;
    private static int _hoverApp = HoverNone;
    private static int _hoverSide = HoverNone;
    private static bool _mouseTracked;

    private static long _lastHideTick;
    private const long ReopenGuardMs = 250;

    private static IntPtr _fontUi;
    private static IntPtr _fontHeading;
    private static IntPtr _brushBg;
    private static IntPtr _brushPanel;
    private static IntPtr _brushHover;
    private static IntPtr _brushBorder;
    private static IntPtr _brushAccent;
    private static IntPtr _brushSearch;

    private static IntPtr _memDc;
    private static IntPtr _memBitmap;
    private static IntPtr _memOldBitmap;
    private static int _memWidth;
    private static int _memHeight;

    private static uint ColorBg => Win32.Rgb(32, 32, 34);
    private static uint ColorPanel => Win32.Rgb(40, 40, 44);
    private static uint ColorBorder => Win32.Rgb(78, 78, 86);
    private static uint ColorHover => Win32.Rgb(58, 58, 64);
    private static uint ColorSearch => Win32.Rgb(24, 24, 26);
    private static uint ColorText => Win32.Rgb(230, 230, 234);
    private static uint ColorTextDim => Win32.Rgb(160, 160, 168);
    private static uint ColorAccent => Win32.Rgb(0, 120, 215);

    private static int Scale(int value) => (int)(value * _dpi / 96.0);

    public static bool IsVisible => _visible;

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
            hbrBackground = IntPtr.Zero,
            lpszClassName = classNamePtr,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            return false;

        _hwnd = Win32.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName, "WinShell Start", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return false;

        _dpi = Win32.GetDpiForWindow(_hwnd);
        if (_dpi == 0)
            _dpi = 96;

        CreateResources();
        return true;
    }

    public static void Destroy()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        Win32.KillTimer(_hwnd, SearchTimerId);
        ReleaseResources();
        AppList.Release();
        FileIcons.Release();
        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    public static void Toggle()
    {
        if (_visible)
        {
            Hide();
            return;
        }

        if (Environment.TickCount64 - _lastHideTick < ReopenGuardMs)
            return;

        Show();
    }

    public static void Show()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        AppList.EnsureLoaded();

        uint dpi = Win32.GetDpiForWindow(_hwnd);
        if (dpi != 0 && dpi != _dpi)
        {
            _dpi = dpi;
            CreateResources();
        }

        _query = string.Empty;
        _scroll = 0;
        _selected = HoverNone;
        _hoverApp = HoverNone;
        _hoverSide = HoverNone;
        FileHits.Clear();
        _searchPending = false;
        Win32.KillTimer(_hwnd, SearchTimerId);
        ApplyFilter();

        int width = Scale(BaseWidth);
        int height = Scale(BaseHeight);

        int left = 0;
        int bottom = Win32.GetSystemMetrics(SM_CYSCREEN) - TaskbarWindow.Height;

        if (Win32.GetWindowRect(TaskbarWindow.Handle, out RECT bar) && bar.Height > 0)
        {
            left = bar.Left;
            bottom = bar.Top;
        }

        int top = bottom - height;

        if (top < 0)
        {
            top = 0;
            height = bottom;
        }

        Win32.SetWindowPos(_hwnd, HWND_TOPMOST, left, top, width, height, SWP_SHOWWINDOW);
        Win32.ShowWindow(_hwnd, SW_SHOW);
        Win32.SetForegroundWindow(_hwnd);

        _visible = true;
        Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public static void Hide()
    {
        if (!_visible)
            return;

        _visible = false;
        _lastHideTick = Environment.TickCount64;
        Win32.ShowWindow(_hwnd, SW_HIDE);
    }

    private static void ApplyFilter()
    {
        Filtered.Clear();

        foreach (AppEntry entry in AppList.Items)
        {
            if (Matches(entry.Name, entry.ParsingName))
            {
                Filtered.Add(new MenuResult
                {
                    Kind = ResultKind.App,
                    Label = entry.Name,
                    App = entry,
                });
            }
        }

        if (_query.Length > 0)
        {
            foreach ((string name, string uri) in SettingsCatalog.Pages)
            {
                if (Matches(name, uri))
                {
                    Filtered.Add(new MenuResult
                    {
                        Kind = ResultKind.Setting,
                        Label = name,
                        Detail = "Settings",
                        Target = uri,
                    });
                }
            }

            foreach ((string path, string name) in FileHits)
            {
                Filtered.Add(new MenuResult
                {
                    Kind = ResultKind.File,
                    Label = name,
                    Detail = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
                    Target = path,
                    IsDirectory = Directory.Exists(path),
                });
            }
        }

        _scroll = 0;
        _selected = Filtered.Count > 0 && _query.Length > 0 ? 0 : HoverNone;
    }

    private static bool Matches(string primary, string secondary)
    {
        if (_query.Length == 0)
            return true;

        foreach (string token in _query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!primary.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !secondary.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void ScheduleFileSearch()
    {
        FileHits.Clear();

        if (_hwnd == IntPtr.Zero)
            return;

        Win32.KillTimer(_hwnd, SearchTimerId);

        if (_query.Length < 3)
        {
            _searchPending = false;
            return;
        }

        _searchPending = true;
        Win32.SetTimer(_hwnd, SearchTimerId, SearchDebounceMs, IntPtr.Zero);
    }

    private static void RunFileSearch(IntPtr hwnd)
    {
        Win32.KillTimer(hwnd, SearchTimerId);
        _searchPending = false;

        FileHits.Clear();
        FileHits.AddRange(FileSearch.Run(_query));

        int previous = _selected;
        ApplyFilter();

        if (previous >= 0 && previous < Filtered.Count)
            _selected = previous;

        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    return new IntPtr(1);

                case WM_PAINT:
                    OnPaint(hwnd);
                    return IntPtr.Zero;

                case WM_ACTIVATE:
                    if (Win32.LoWord(wParam) == WA_INACTIVE)
                        Hide();

                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    OnMouseMove(hwnd, Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_MOUSELEAVE:
                    _mouseTracked = false;
                    SetHover(hwnd, HoverNone, HoverNone);
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    OnClick(Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_MOUSEWHEEL:
                    OnWheel(hwnd, Win32.HiWord(wParam));
                    return IntPtr.Zero;

                case WM_CHAR:
                    OnChar(hwnd, (char)(long)wParam);
                    return IntPtr.Zero;

                case WM_KEYDOWN:
                    OnKeyDown(hwnd, (int)(long)wParam);
                    return IntPtr.Zero;

                case WM_TIMER:
                    if ((long)wParam == (long)SearchTimerId)
                        RunFileSearch(hwnd);

                    return IntPtr.Zero;

                case WM_CLOSE:
                    Hide();
                    return IntPtr.Zero;
            }
        }
        catch
        {
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OnChar(IntPtr hwnd, char ch)
    {
        if (ch == '\b')
        {
            if (_query.Length > 0)
                _query = _query[..^1];
        }
        else if (!char.IsControl(ch))
        {
            _query += ch;
        }
        else
        {
            return;
        }

        ScheduleFileSearch();
        ApplyFilter();
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void OnKeyDown(IntPtr hwnd, int key)
    {
        int rows = VisibleRows();

        switch (key)
        {
            case VK_ESCAPE:
                if (_query.Length > 0)
                {
                    _query = string.Empty;
                    ScheduleFileSearch();
                    ApplyFilter();
                    break;
                }

                Hide();
                return;

            case VK_RETURN:
                if (_selected >= 0)
                    LaunchApp(_selected);

                return;

            case VK_DOWN:
                Move(1);
                break;

            case VK_UP:
                Move(-1);
                break;

            case VK_NEXT:
                Move(rows);
                break;

            case VK_PRIOR:
                Move(-rows);
                break;

            default:
                return;
        }

        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void Move(int delta)
    {
        if (Filtered.Count == 0)
            return;

        _selected = Math.Clamp(_selected < 0 ? 0 : _selected + delta, 0, Filtered.Count - 1);
        ScrollIntoView();
    }

    private static void ScrollIntoView()
    {
        int rows = VisibleRows();
        if (rows <= 0)
            return;

        if (_selected < _scroll)
            _scroll = _selected;
        else if (_selected >= _scroll + rows)
            _scroll = _selected - rows + 1;

        ClampScroll();
    }

    private static void OnWheel(IntPtr hwnd, int delta)
    {
        _scroll -= delta / 120 * 3;
        ClampScroll();
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void ClampScroll()
    {
        int max = Math.Max(0, Filtered.Count - VisibleRows());
        _scroll = Math.Clamp(_scroll, 0, max);
    }

    private static int VisibleRows()
    {
        if (_hwnd == IntPtr.Zero)
            return 0;

        Win32.GetClientRect(_hwnd, out RECT client);
        LayoutRegions(client.Width, client.Height, out RECT list, out _, out _);
        return Math.Max(0, list.Height / Scale(BaseRowHeight));
    }

    private static void OnMouseMove(IntPtr hwnd, int x, int y)
    {
        if (!_mouseTracked)
        {
            var track = new TRACKMOUSEEVENT
            {
                cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                dwFlags = TME_LEAVE,
                hwndTrack = hwnd,
            };

            Win32.TrackMouseEvent(ref track);
            _mouseTracked = true;
        }

        HitTest(x, y, out int app, out int side);
        SetHover(hwnd, app, side);
    }

    private static void SetHover(IntPtr hwnd, int app, int side)
    {
        if (_hoverApp == app && _hoverSide == side)
            return;

        _hoverApp = app;
        _hoverSide = side;
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void OnClick(int x, int y)
    {
        HitTest(x, y, out int app, out int side);

        if (app >= 0)
        {
            LaunchApp(app);
            return;
        }

        if (side >= 0)
            RunSide(Side[side]);
    }

    private static void HitTest(int x, int y, out int app, out int side)
    {
        app = HoverNone;
        side = HoverNone;

        Win32.GetClientRect(_hwnd, out RECT client);
        Layout(client.Width, client.Height);

        for (int i = 0; i < Filtered.Count; i++)
        {
            if (Filtered[i].Bounds.Width > 0 && Filtered[i].Bounds.Contains(x, y))
            {
                app = i;
                return;
            }
        }

        for (int i = 0; i < Side.Length; i++)
        {
            if (Side[i].Bounds.Contains(x, y))
            {
                side = i;
                return;
            }
        }
    }

    private static void LaunchApp(int index)
    {
        if (index < 0 || index >= Filtered.Count)
            return;

        MenuResult result = Filtered[index];
        Hide();

        switch (result.Kind)
        {
            case ResultKind.App when result.App != null:
                AppList.Launch(result.App);
                break;

            case ResultKind.Setting:
            case ResultKind.File:
                ShellLaunch.Open(result.Target);
                break;
        }
    }

    private static void RunSide(SideItem item)
    {
        Hide();

        switch (item.Kind)
        {
            case ActionKind.Open:
                ShellLaunch.Open(item.Target);
                break;

            case ActionKind.Settings:
                SettingsWindow.Show();
                break;

            case ActionKind.Lock:
                Win32.LockWorkStation();
                break;

            case ActionKind.SignOut:
                Win32.ExitWindowsEx(EWX_LOGOFF, 0);
                break;

            case ActionKind.Restart:
                Win32.ShellExecute(IntPtr.Zero, null, "shutdown.exe", "/r /t 0", null, 0);
                break;

            case ActionKind.Shutdown:
                Win32.ShellExecute(IntPtr.Zero, null, "shutdown.exe", "/s /t 0", null, 0);
                break;
        }
    }

    private static void LayoutRegions(int width, int height, out RECT list, out RECT search, out RECT right)
    {
        int pad = Scale(BasePad);
        int leftWidth = Scale(BaseLeftWidth);
        int searchHeight = Scale(BaseSearchHeight);

        search = new RECT(pad, height - pad - searchHeight, pad + leftWidth, height - pad);
        list = new RECT(pad, pad, pad + leftWidth, search.Top - pad);
        right = new RECT(pad + leftWidth + pad, pad, width - pad, height - pad);
    }

    private static void Layout(int width, int height)
    {
        LayoutRegions(width, height, out RECT list, out _, out RECT right);

        int rowHeight = Scale(BaseRowHeight);
        int rows = Math.Max(0, list.Height / rowHeight);

        for (int i = 0; i < Filtered.Count; i++)
        {
            int row = i - _scroll;

            if (row < 0 || row >= rows)
            {
                Filtered[i].Bounds = default;
                continue;
            }

            int top = list.Top + (row * rowHeight);
            Filtered[i].Bounds = new RECT(list.Left, top, list.Right, top + rowHeight);
        }

        int sideHeight = Scale(BaseRowHeight);
        int powerCount = 0;

        foreach (SideItem item in Side)
        {
            if (item.IsPower)
                powerCount++;
        }

        int topIndex = 0;
        int powerIndex = 0;

        foreach (SideItem item in Side)
        {
            int top;
            if (item.IsPower)
            {
                top = right.Bottom - ((powerCount - powerIndex) * sideHeight);
                powerIndex++;
            }
            else
            {
                top = right.Top + (topIndex * sideHeight);
                topIndex++;
            }

            item.Bounds = new RECT(right.Left, top, right.Right, top + sideHeight);
        }
    }

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

        var border = new RECT(0, 0, width, 1);
        Win32.FillRect(dc, ref border, _brushBorder);
        border = new RECT(0, height - 1, width, height);
        Win32.FillRect(dc, ref border, _brushBorder);
        border = new RECT(0, 0, 1, height);
        Win32.FillRect(dc, ref border, _brushBorder);
        border = new RECT(width - 1, 0, width, height);
        Win32.FillRect(dc, ref border, _brushBorder);

        LayoutRegions(width, height, out RECT list, out RECT search, out RECT right);
        Layout(width, height);

        Win32.FillRect(dc, ref list, _brushPanel);

        PaintApps(dc, list);
        PaintSearch(dc, search);
        PaintSide(dc, right);
    }

    private static void PaintApps(IntPtr dc, RECT list)
    {
        IntPtr previousFont = Win32.SelectObject(dc, _fontUi);

        int iconSize = Scale(BaseIconSize);
        int pad = Scale(BasePad);

        if (Filtered.Count == 0)
        {
            string message = AppList.Items.Count == 0 ? "No apps found."
                : _searchPending ? "Searching files..."
                : "No matches.";

            var empty = new RECT(list.Left + pad, list.Top + pad, list.Right - pad, list.Top + pad + Scale(BaseRowHeight));
            DrawLabel(dc, message, ref empty,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorTextDim);
            Win32.SelectObject(dc, previousFont);
            return;
        }

        for (int i = 0; i < Filtered.Count; i++)
        {
            MenuResult result = Filtered[i];
            RECT bounds = result.Bounds;

            if (bounds.Width <= 0)
                continue;

            if (i == _selected)
            {
                Win32.FillRect(dc, ref bounds, _brushAccent);
            }
            else if (i == _hoverApp)
            {
                Win32.FillRect(dc, ref bounds, _brushHover);
            }

            int x = bounds.Left + Scale(6);

            IntPtr icon = result.Kind switch
            {
                ResultKind.App when result.App != null => AppList.Icon(result.App),
                ResultKind.File => FileIcons.For(result.Target, result.IsDirectory),
                _ => IntPtr.Zero,
            };

            if (icon != IntPtr.Zero)
            {
                int iconY = bounds.Top + ((bounds.Height - iconSize) / 2);
                Win32.DrawIconEx(dc, x, iconY, icon, iconSize, iconSize, 0, IntPtr.Zero, DI_NORMAL);
            }

            x += iconSize + Scale(8);

            int detailWidth = result.Detail.Length > 0 ? Scale(88) : 0;
            var textRect = new RECT(x, bounds.Top, bounds.Right - Scale(6) - detailWidth, bounds.Bottom);
            DrawLabel(dc, result.Label, ref textRect,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX, ColorText);

            if (detailWidth > 0)
            {
                var detailRect = new RECT(bounds.Right - Scale(6) - detailWidth, bounds.Top, bounds.Right - Scale(6), bounds.Bottom);
                DrawLabel(dc, result.Detail, ref detailRect,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX, ColorTextDim);
            }
        }

        Win32.SelectObject(dc, previousFont);
    }

    private static void PaintSearch(IntPtr dc, RECT rect)
    {
        Win32.FillRect(dc, ref rect, _brushSearch);

        var line = new RECT(rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom);
        Win32.FillRect(dc, ref line, _query.Length > 0 ? _brushAccent : _brushBorder);

        IntPtr previousFont = Win32.SelectObject(dc, _fontUi);

        var textRect = new RECT(rect.Left + Scale(8), rect.Top, rect.Right - Scale(8), rect.Bottom);

        if (_query.Length == 0)
        {
            DrawLabel(dc, "Search programs...", ref textRect,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorTextDim);
        }
        else
        {
            DrawLabel(dc, _query + "|", ref textRect,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX, ColorText);
        }

        Win32.SelectObject(dc, previousFont);
    }

    private static void PaintSide(IntPtr dc, RECT right)
    {
        IntPtr previousFont = Win32.SelectObject(dc, _fontHeading);

        for (int i = 0; i < Side.Length; i++)
        {
            SideItem item = Side[i];
            RECT bounds = item.Bounds;

            if (i == _hoverSide)
                Win32.FillRect(dc, ref bounds, _brushHover);

            var textRect = new RECT(bounds.Left + Scale(6), bounds.Top, bounds.Right - Scale(6), bounds.Bottom);
            DrawLabel(dc, item.Label, ref textRect,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX,
                item.IsPower ? ColorTextDim : ColorText);
        }

        foreach (SideItem item in Side)
        {
            if (!item.IsPower)
                continue;

            var rule = new RECT(right.Left, item.Bounds.Top - Scale(5), right.Right, item.Bounds.Top - Scale(4));
            Win32.FillRect(dc, ref rule, _brushBorder);
            break;
        }

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

        Win32.SelectObject(_memDc, _memOldBitmap);
        Win32.DeleteObject(_memBitmap);
        Win32.DeleteDC(_memDc);

        _memDc = IntPtr.Zero;
        _memBitmap = IntPtr.Zero;
        _memOldBitmap = IntPtr.Zero;
        _memWidth = 0;
        _memHeight = 0;
    }

    private static void CreateResources()
    {
        ReleaseResources();

        _fontUi = CreateUiFont(Scale(12), 400);
        _fontHeading = CreateUiFont(Scale(12), 600);

        _brushBg = Win32.CreateSolidBrush(ColorBg);
        _brushPanel = Win32.CreateSolidBrush(ColorPanel);
        _brushHover = Win32.CreateSolidBrush(ColorHover);
        _brushBorder = Win32.CreateSolidBrush(ColorBorder);
        _brushAccent = Win32.CreateSolidBrush(ColorAccent);
        _brushSearch = Win32.CreateSolidBrush(ColorSearch);
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
            _fontUi, _fontHeading,
            _brushBg, _brushPanel, _brushHover, _brushBorder, _brushAccent, _brushSearch,
        ];

        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
                Win32.DeleteObject(handle);
        }

        _fontUi = _fontHeading = IntPtr.Zero;
        _brushBg = _brushPanel = _brushHover = _brushBorder = _brushAccent = _brushSearch = IntPtr.Zero;

        ReleaseBackBuffer();
    }
}
