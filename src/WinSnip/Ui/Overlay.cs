using System.Runtime.InteropServices;
using WinSnip.Native;
using static WinSnip.Native.Win32Const;

namespace WinSnip.Ui;

internal enum OverlayMode
{
    Region,
    WindowPick,
}

// Full-virtual-desktop layered window used to pick a region or a window.
//
// Per-pixel alpha via UpdateLayeredWindow rather than a painted backdrop: the alternative is to
// capture the screen first and draw that bitmap as the background, which is what Snipping Tool
// does, but it doubles the capture cost and goes stale the moment anything animates underneath.
// A layered window dims the live desktop instead.
internal static unsafe class Overlay
{
    private const string ClassName = "WinSnip.Overlay";

    // Premultiplied BGRA, written as 0xAARRGGBB.
    private const uint DimPixel = 0x78000000;

    // The "hole" is alpha 1, not alpha 0, on purpose: a layered window treats fully transparent
    // pixels as click-through, so a genuine hole would send hover and click messages to the window
    // underneath instead of to the overlay. Alpha 1 is visually indistinguishable but still hits.
    private const uint HolePixel = 0x01000000;
    private const uint BorderPixel = 0xFFFFFFFF;

    private const int BorderThickness = 2;

    private static IntPtr _hwnd;
    private static IntPtr _dib;
    private static IntPtr _memoryDc;
    private static uint* _pixels;

    private static int _originX;
    private static int _originY;
    private static int _width;
    private static int _height;

    private static OverlayMode _mode;
    private static bool _dragging;
    private static POINT _anchor;
    private static RECT _highlight;
    private static bool _hasHighlight;

    private static Action<RECT>? _onRegion;
    private static Action<IntPtr>? _onWindow;

    public static bool Active => _hwnd != IntPtr.Zero;

    public static void ShowRegion(Action<RECT> onPicked)
    {
        _onRegion = onPicked;
        _onWindow = null;
        Show(OverlayMode.Region);
    }

    public static void ShowWindowPick(Action<IntPtr> onPicked)
    {
        _onRegion = null;
        _onWindow = onPicked;
        Show(OverlayMode.WindowPick);
    }

    private static void Show(OverlayMode mode)
    {
        if (Active)
            return;

        _mode = mode;
        _dragging = false;
        _hasHighlight = false;
        _highlight = default;

        // Virtual-screen metrics, not primary-screen: the overlay has to span every monitor or a
        // selection started on a secondary display would fall outside it.
        _originX = Win32.GetSystemMetrics(SM_XVIRTUALSCREEN);
        _originY = Win32.GetSystemMetrics(SM_YVIRTUALSCREEN);
        _width = Win32.GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _height = Win32.GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (_width <= 0 || _height <= 0)
            return;

        EnsureClass();

        _hwnd = Win32.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            ClassName, "WinSnip", WS_POPUP,
            _originX, _originY, _width, _height,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return;

        if (!CreateSurface())
        {
            Destroy();
            return;
        }

        Win32.ShowWindow(_hwnd, SW_SHOW);
        Win32.SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

        // Foreground so Esc reaches us as a WM_KEYDOWN rather than going to whatever was focused.
        Win32.SetForegroundWindow(_hwnd);
        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, new IntPtr(mode == OverlayMode.Region ? IDC_CROSS : IDC_ARROW)));

        if (mode == OverlayMode.WindowPick)
            UpdateHover();

        Paint();
    }

    private static bool _classRegistered;

    private static void EnsureClass()
    {
        if (_classRegistered)
            return;

        // Leaked on purpose - the class outlives every overlay instance and is freed at exit.
        IntPtr namePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0,
            lpfnWndProc = &WndProc,
            hInstance = Win32.GetModuleHandle(null),
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_CROSS)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = namePtr,
        };

        _classRegistered = Win32.RegisterClassEx(ref wc) != 0;
    }

    private static bool CreateSurface()
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            // Negative height makes the DIB top-down, so row 0 is the top of the screen and the
            // pixel index maths below is the obvious one rather than being flipped.
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = _width,
                biHeight = -_height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };

            _dib = Win32.CreateDIBSection(screenDc, &header, DIB_RGB_COLORS, out void* bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero)
                return false;

            _pixels = (uint*)bits;
            _memoryDc = Win32.CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
                return false;

            Win32.SelectObject(_memoryDc, _dib);
            return true;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void Paint()
    {
        if (_pixels is null)
            return;

        int total = _width * _height;
        for (int i = 0; i < total; i++)
            _pixels[i] = DimPixel;

        if (_hasHighlight)
        {
            RECT r = ToClient(_highlight);
            FillRect(r, HolePixel);
            StrokeRect(r, BorderPixel, BorderThickness);
        }

        var destination = new POINT { X = _originX, Y = _originY };
        var size = new SIZE { Cx = _width, Cy = _height };
        var source = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        Win32.UpdateLayeredWindow(
            _hwnd, IntPtr.Zero, &destination, &size, _memoryDc, &source, 0, &blend, ULW_ALPHA);
    }

    private static RECT ToClient(RECT screen) =>
        new(screen.Left - _originX, screen.Top - _originY, screen.Right - _originX, screen.Bottom - _originY);

    private static void FillRect(RECT r, uint colour)
    {
        int left = Math.Clamp(r.Left, 0, _width);
        int top = Math.Clamp(r.Top, 0, _height);
        int right = Math.Clamp(r.Right, 0, _width);
        int bottom = Math.Clamp(r.Bottom, 0, _height);

        for (int y = top; y < bottom; y++)
        {
            uint* row = _pixels + ((long)y * _width);
            for (int x = left; x < right; x++)
                row[x] = colour;
        }
    }

    private static void StrokeRect(RECT r, uint colour, int thickness)
    {
        FillRect(new RECT(r.Left, r.Top, r.Right, r.Top + thickness), colour);
        FillRect(new RECT(r.Left, r.Bottom - thickness, r.Right, r.Bottom), colour);
        FillRect(new RECT(r.Left, r.Top, r.Left + thickness, r.Bottom), colour);
        FillRect(new RECT(r.Right - thickness, r.Top, r.Right, r.Bottom), colour);
    }

    private static void UpdateHover()
    {
        Win32.GetCursorPos(out POINT cursor);

        if (WindowPicker.TryPick(cursor, _hwnd, out IntPtr _, out RECT bounds))
        {
            _highlight = bounds;
            _hasHighlight = true;
        }
        else
        {
            _hasHighlight = false;
        }
    }

    private static void Finish()
    {
        RECT region = _highlight;
        bool has = _hasHighlight;
        OverlayMode mode = _mode;
        Action<RECT>? onRegion = _onRegion;
        Action<IntPtr>? onWindow = _onWindow;

        IntPtr picked = IntPtr.Zero;
        if (mode == OverlayMode.WindowPick && has)
        {
            Win32.GetCursorPos(out POINT cursor);
            WindowPicker.TryPick(cursor, _hwnd, out picked, out _);
        }

        Destroy();

        if (!has)
            return;

        if (mode == OverlayMode.Region)
            onRegion?.Invoke(region);
        else if (picked != IntPtr.Zero)
            onWindow?.Invoke(picked);
    }

    private static void Cancel() => Destroy();

    private static void Destroy()
    {
        if (_dragging)
        {
            Win32.ReleaseCapture();
            _dragging = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_memoryDc != IntPtr.Zero)
        {
            Win32.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            Win32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _pixels = null;
        _hasHighlight = false;
        _onRegion = null;
        _onWindow = null;
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

                case WM_KEYDOWN when (int)wParam == VK_ESCAPE:
                    Cancel();
                    return IntPtr.Zero;

                case WM_LBUTTONDOWN:
                    if (_mode == OverlayMode.Region)
                    {
                        Win32.GetCursorPos(out _anchor);
                        _dragging = true;
                        _hasHighlight = false;
                        Win32.SetCapture(hwnd);
                    }
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    if (_mode == OverlayMode.WindowPick)
                    {
                        RECT before = _highlight;
                        bool had = _hasHighlight;
                        UpdateHover();
                        if (!had
                            || _hasHighlight != had
                            || before.Left != _highlight.Left
                            || before.Top != _highlight.Top
                            || before.Right != _highlight.Right
                            || before.Bottom != _highlight.Bottom)
                        {
                            Paint();
                        }
                    }
                    else if (_dragging)
                    {
                        Win32.GetCursorPos(out POINT cursor);
                        _highlight = RECT.FromPoints(_anchor.X, _anchor.Y, cursor.X, cursor.Y);
                        _hasHighlight = _highlight.Width > 0 && _highlight.Height > 0;
                        Paint();
                    }
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    if (_mode == OverlayMode.Region && _dragging)
                    {
                        Win32.ReleaseCapture();
                        _dragging = false;
                        Finish();
                    }
                    else if (_mode == OverlayMode.WindowPick)
                    {
                        Finish();
                    }
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    Cancel();
                    return IntPtr.Zero;

                case WM_SETCURSOR:
                    Win32.SetCursor(Win32.LoadCursor(
                        IntPtr.Zero, new IntPtr(_mode == OverlayMode.Region ? IDC_CROSS : IDC_ARROW)));
                    return new IntPtr(1);
            }
        }
        catch
        {
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
