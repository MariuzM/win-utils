using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal static unsafe class SettingsWindow
{
    private const string ClassName = "WinShellSettings";

    private const int BaseWidth = 560;
    private const int BaseHeight = 430;
    private const int BasePad = 20;
    private const int BaseButtonHeight = 32;
    private const int BaseButtonWidth = 210;
    private const int BaseLine = 20;

    private enum Action
    {
        InstallShell,
        RestoreExplorer,
        DisableIndexing,
        Close,
    }

    private sealed class Button
    {
        public Action Action;
        public string Label = string.Empty;
        public bool Enabled = true;
        public RECT Bounds;
    }

    private static readonly Button[] Buttons =
    [
        new() { Action = Action.InstallShell, Label = "Install WinShell as shell" },
        new() { Action = Action.RestoreExplorer, Label = "Restore Explorer as shell" },
        new() { Action = Action.DisableIndexing, Label = "Disable file indexing" },
        new() { Action = Action.Close, Label = "Close" },
    ];

    private static IntPtr _hwnd;
    private static uint _dpi = 96;
    private static bool _visible;
    private static int _hover = -1;
    private static bool _mouseTracked;
    private static string _result = string.Empty;

    private static IntPtr _fontUi;
    private static IntPtr _fontBold;
    private static IntPtr _brushBg;
    private static IntPtr _brushPanel;
    private static IntPtr _brushBorder;
    private static IntPtr _brushHover;
    private static IntPtr _brushAccent;

    private static IntPtr _memDc;
    private static IntPtr _memBitmap;
    private static IntPtr _memOldBitmap;
    private static int _memWidth;
    private static int _memHeight;

    private static uint ColorBg => Win32.Rgb(32, 32, 34);
    private static uint ColorPanel => Win32.Rgb(40, 40, 44);
    private static uint ColorBorder => Win32.Rgb(78, 78, 86);
    private static uint ColorHover => Win32.Rgb(58, 58, 64);
    private static uint ColorAccent => Win32.Rgb(0, 120, 215);
    private static uint ColorText => Win32.Rgb(230, 230, 234);
    private static uint ColorDim => Win32.Rgb(165, 165, 173);
    private static uint ColorWarn => Win32.Rgb(240, 190, 100);

    private static int Scale(int value) => (int)(value * _dpi / 96.0);

    public static bool Create()
    {
        IntPtr instance = Win32.GetModuleHandle(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0,
            lpfnWndProc = &WndProc,
            hInstance = instance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            return false;

        _hwnd = Win32.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName, "WinShell Settings", WS_POPUP,
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

        ReleaseResources();
        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    public static void Show()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        uint dpi = Win32.GetDpiForWindow(_hwnd);
        if (dpi != 0 && dpi != _dpi)
        {
            _dpi = dpi;
            CreateResources();
        }

        _result = string.Empty;

        int width = Scale(BaseWidth);
        int height = Scale(BaseHeight);
        int left = (Win32.GetSystemMetrics(SM_CXSCREEN) - width) / 2;
        int top = (Win32.GetSystemMetrics(SM_CYSCREEN) - height) / 2;

        Win32.SetWindowPos(_hwnd, HWND_TOPMOST, left, top, width, height, SWP_SHOWWINDOW);
        Win32.ShowWindow(_hwnd, SW_SHOW);
        Win32.SetForegroundWindow(_hwnd);

        _visible = true;
        Win32.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public static bool Standalone { get; set; }

    public static void Hide()
    {
        if (!_visible)
            return;

        _visible = false;
        Win32.ShowWindow(_hwnd, SW_HIDE);

        // Launched via --settings there is no taskbar behind us, so closing the window has to
        // end the message loop or the process would linger with nothing visible.
        if (Standalone)
            Win32.PostQuitMessage(0);
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

                case WM_MOUSEMOVE:
                    OnMouseMove(hwnd, Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_MOUSELEAVE:
                    _mouseTracked = false;
                    SetHover(hwnd, -1);
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    OnClick(hwnd, Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_KEYDOWN:
                    if ((int)(long)wParam == VK_ESCAPE)
                        Hide();

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

        SetHover(hwnd, HitTest(x, y));
    }

    private static void SetHover(IntPtr hwnd, int index)
    {
        if (_hover == index)
            return;

        _hover = index;
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static int HitTest(int x, int y)
    {
        Win32.GetClientRect(_hwnd, out RECT client);
        Layout(client.Width, client.Height);

        for (int i = 0; i < Buttons.Length; i++)
        {
            if (Buttons[i].Enabled && Buttons[i].Bounds.Contains(x, y))
                return i;
        }

        return -1;
    }

    private static void OnClick(IntPtr hwnd, int x, int y)
    {
        int index = HitTest(x, y);
        if (index < 0)
            return;

        switch (Buttons[index].Action)
        {
            case Action.InstallShell:
                ShellRegistration.InstallToLocal(out _result);
                break;

            case Action.RestoreExplorer:
                ShellRegistration.Uninstall(out _result);
                break;

            case Action.DisableIndexing:
                SearchControl.Disable(out _result);
                break;

            case Action.Close:
                Hide();
                return;
        }

        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void Layout(int width, int height)
    {
        int pad = Scale(BasePad);
        int buttonWidth = Scale(BaseButtonWidth);
        int buttonHeight = Scale(BaseButtonHeight);
        int gap = Scale(10);

        int row = Scale(150);
        Buttons[0].Bounds = new RECT(pad, row, pad + buttonWidth, row + buttonHeight);
        Buttons[1].Bounds = new RECT(pad + buttonWidth + gap, row, pad + (buttonWidth * 2) + gap, row + buttonHeight);

        row = Scale(250);
        Buttons[2].Bounds = new RECT(pad, row, pad + buttonWidth, row + buttonHeight);

        Buttons[3].Bounds = new RECT(
            width - pad - Scale(100), height - pad - buttonHeight,
            width - pad, height - pad);
    }

    private static void OnPaint(IntPtr hwnd)
    {
        IntPtr hdc = Win32.BeginPaint(hwnd, out PAINTSTRUCT ps);
        Win32.GetClientRect(hwnd, out RECT client);

        if (client.Width > 0 && client.Height > 0)
        {
            EnsureBackBuffer(hdc, client.Width, client.Height);
            Paint(_memDc, client.Width, client.Height);
            Win32.BitBlt(hdc, 0, 0, client.Width, client.Height, _memDc, 0, 0, SRCCOPY);
        }

        Win32.EndPaint(hwnd, ref ps);
    }

    private static void Paint(IntPtr dc, int width, int height)
    {
        Win32.SetBkMode(dc, TRANSPARENT);

        var full = new RECT(0, 0, width, height);
        Win32.FillRect(dc, ref full, _brushBg);

        var edge = new RECT(0, 0, width, 1);
        Win32.FillRect(dc, ref edge, _brushBorder);
        edge = new RECT(0, height - 1, width, height);
        Win32.FillRect(dc, ref edge, _brushBorder);
        edge = new RECT(0, 0, 1, height);
        Win32.FillRect(dc, ref edge, _brushBorder);
        edge = new RECT(width - 1, 0, width, height);
        Win32.FillRect(dc, ref edge, _brushBorder);

        Layout(width, height);

        int pad = Scale(BasePad);
        int line = Scale(BaseLine);
        int y = pad;

        IntPtr previous = Win32.SelectObject(dc, _fontBold);
        Draw(dc, "WinShell Settings", pad, y, width - pad, line, ColorText);
        y += Scale(34);

        Draw(dc, "Shell", pad, y, width - pad, line, ColorAccent);
        y += Scale(24);

        Win32.SelectObject(dc, _fontUi);

        bool installed = ShellRegistration.IsInstalled;
        Draw(dc, installed
            ? "WinShell is registered as your shell. Takes effect at next sign-in."
            : "Explorer is your shell. WinShell is running as a normal app.",
            pad, y, width - pad, line, installed ? ColorAccent : ColorDim);
        y += line;

        Draw(dc, "Installing copies WinShell to your local disk and registers that copy.",
            pad, y, width - pad, line, ColorDim);

        y = Scale(200);
        Win32.SelectObject(dc, _fontBold);
        Draw(dc, "Search", pad, y, width - pad, line, ColorAccent);
        Win32.SelectObject(dc, _fontUi);
        y += Scale(24);
        Draw(dc, "Indexing needs administrator rights; a UAC prompt will appear.",
            pad, y, width - pad, line, ColorDim);

        PaintButtons(dc);

        int warnTop = Scale(300);
        var warnRect = new RECT(pad, warnTop, width - pad, warnTop + Scale(76));
        Win32.FillRect(dc, ref warnRect, _brushPanel);

        var warnInner = new RECT(pad + Scale(10), warnTop + Scale(8), width - pad - Scale(10), warnTop + Scale(70));
        DrawWrapped(dc,
            "Running as the shell removes the desktop, wallpaper and desktop icons - those belong to Explorer. "
            + "If you are ever left at a blank screen, press Ctrl+Shift+Esc for Task Manager, then File > Run new task > explorer.exe.",
            ref warnInner, ColorWarn);

        if (_result.Length > 0)
        {
            int resultTop = Scale(386);
            var resultRect = new RECT(pad, resultTop, width - pad - Scale(110), resultTop + Scale(44));
            DrawWrapped(dc, _result, ref resultRect, ColorText);
        }

        Win32.SelectObject(dc, previous);
    }

    private static void PaintButtons(IntPtr dc)
    {
        for (int i = 0; i < Buttons.Length; i++)
        {
            Button button = Buttons[i];
            RECT bounds = button.Bounds;

            Win32.FillRect(dc, ref bounds, i == _hover ? _brushHover : _brushPanel);

            var top = new RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Top + 1);
            Win32.FillRect(dc, ref top, _brushBorder);
            var bottom = new RECT(bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom);
            Win32.FillRect(dc, ref bottom, _brushBorder);

            DrawLabel(dc, button.Label, ref bounds,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorText);
        }
    }

    private static void Draw(IntPtr dc, string text, int left, int top, int right, int height, uint color)
    {
        var rect = new RECT(left, top, right, top + height);
        DrawLabel(dc, text, ref rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS, color);
    }

    private static void DrawWrapped(IntPtr dc, string text, ref RECT rect, uint color)
    {
        Win32.SetTextColor(dc, color);
        fixed (char* p = text)
        {
            Win32.DrawText(dc, p, text.Length, ref rect, DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);
        }
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

        const uint DEFAULT_CHARSET = 1;
        const uint CLEARTYPE_QUALITY = 5;

        _fontUi = Win32.CreateFont(
            -Scale(12), 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        _fontBold = Win32.CreateFont(
            -Scale(13), 0, 0, 0, 600, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");

        _brushBg = Win32.CreateSolidBrush(ColorBg);
        _brushPanel = Win32.CreateSolidBrush(ColorPanel);
        _brushBorder = Win32.CreateSolidBrush(ColorBorder);
        _brushHover = Win32.CreateSolidBrush(ColorHover);
        _brushAccent = Win32.CreateSolidBrush(ColorAccent);
    }

    private static void ReleaseResources()
    {
        IntPtr[] handles = [_fontUi, _fontBold, _brushBg, _brushPanel, _brushBorder, _brushHover, _brushAccent];

        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
                Win32.DeleteObject(handle);
        }

        _fontUi = _fontBold = IntPtr.Zero;
        _brushBg = _brushPanel = _brushBorder = _brushHover = _brushAccent = IntPtr.Zero;

        ReleaseBackBuffer();
    }
}
