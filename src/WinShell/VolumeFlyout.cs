using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal static unsafe class VolumeFlyout
{
    private const string ClassName = "WinShellVolumeFlyout";

    private const int BaseWidth = 260;
    private const int BaseHeight = 76;
    private const int BasePad = 14;
    private const int BaseGlyphWidth = 30;
    private const int BaseValueWidth = 46;
    private const int BaseTrackHeight = 4;
    private const int BaseThumbWidth = 10;
    private const int BaseThumbHeight = 20;

    private static IntPtr _hwnd;
    private static uint _dpi = 96;
    private static bool _visible;
    private static bool _dragging;
    private static long _lastHideTick;
    private const long ReopenGuardMs = 250;

    private static IntPtr _fontUi;
    private static IntPtr _fontGlyph;
    private static IntPtr _brushBg;
    private static IntPtr _brushBorder;
    private static IntPtr _brushTrack;
    private static IntPtr _brushAccent;
    private static IntPtr _brushThumb;

    private static IntPtr _memDc;
    private static IntPtr _memBitmap;
    private static IntPtr _memOldBitmap;
    private static int _memWidth;
    private static int _memHeight;

    private static uint ColorBg => Win32.Rgb(40, 40, 44);
    private static uint ColorBorder => Win32.Rgb(78, 78, 86);
    private static uint ColorTrack => Win32.Rgb(72, 72, 80);
    private static uint ColorAccent => Win32.Rgb(0, 120, 215);
    private static uint ColorThumb => Win32.Rgb(235, 235, 240);
    private static uint ColorText => Win32.Rgb(230, 230, 234);

    private static int Scale(int value) => (int)(value * _dpi / 96.0);

    public static bool IsVisible => _visible;

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
            ClassName, "Volume", WS_POPUP,
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

    public static void Toggle(RECT anchorScreen)
    {
        if (_visible)
        {
            Hide();
            return;
        }

        if (Environment.TickCount64 - _lastHideTick < ReopenGuardMs)
            return;

        Show(anchorScreen);
    }

    public static void Show(RECT anchorScreen)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        uint dpi = Win32.GetDpiForWindow(_hwnd);
        if (dpi != 0 && dpi != _dpi)
        {
            _dpi = dpi;
            CreateResources();
        }

        int width = Scale(BaseWidth);
        int height = Scale(BaseHeight);

        int left = anchorScreen.Left + (anchorScreen.Width / 2) - (width / 2);
        int top = anchorScreen.Top - height - Scale(6);

        int screenWidth = Win32.GetSystemMetrics(SM_CXSCREEN);
        left = Math.Clamp(left, Scale(4), screenWidth - width - Scale(4));

        if (top < 0)
            top = anchorScreen.Bottom + Scale(6);

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

        if (_dragging)
        {
            Win32.ReleaseCapture();
            _dragging = false;
        }

        _visible = false;
        _lastHideTick = Environment.TickCount64;
        Win32.ShowWindow(_hwnd, SW_HIDE);
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

                case WM_LBUTTONDOWN:
                    OnPress(hwnd, Win32.LoWord(lParam), Win32.HiWord(lParam));
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    if (_dragging)
                        ApplyFromX(hwnd, Win32.LoWord(lParam));

                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    if (_dragging)
                    {
                        Win32.ReleaseCapture();
                        _dragging = false;
                    }

                    return IntPtr.Zero;

                case WM_MOUSEWHEEL:
                    SystemIndicators.AdjustVolume(Win32.HiWord(wParam) / 120);
                    SystemIndicators.Refresh();
                    TaskbarWindow.Invalidate();
                    Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
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

    private static void OnPress(IntPtr hwnd, int x, int y)
    {
        Win32.GetClientRect(hwnd, out RECT client);
        Layout(client.Width, client.Height, out RECT glyph, out RECT track, out _);

        if (glyph.Contains(x, y))
        {
            SystemIndicators.ToggleMute();
            SystemIndicators.Refresh();
            TaskbarWindow.Invalidate();
            Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
            return;
        }

        // Anywhere along the slider row counts, not just the thumb - a 4 px track is far too
        // small a target to require hitting exactly.
        if (y >= track.Top - Scale(14) && y <= track.Bottom + Scale(14))
        {
            _dragging = true;
            Win32.SetCapture(hwnd);
            ApplyFromX(hwnd, x);
        }
    }

    private static void ApplyFromX(IntPtr hwnd, int x)
    {
        Win32.GetClientRect(hwnd, out RECT client);
        Layout(client.Width, client.Height, out _, out RECT track, out _);

        if (track.Width <= 0)
            return;

        float fraction = Math.Clamp((x - track.Left) / (float)track.Width, 0f, 1f);
        SystemIndicators.SetVolume(fraction);
        SystemIndicators.Refresh();
        TaskbarWindow.Invalidate();
        Win32.InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void Layout(int width, int height, out RECT glyph, out RECT track, out RECT value)
    {
        int pad = Scale(BasePad);
        int glyphWidth = Scale(BaseGlyphWidth);
        int valueWidth = Scale(BaseValueWidth);
        int trackHeight = Scale(BaseTrackHeight);

        glyph = new RECT(pad, 0, pad + glyphWidth, height);
        value = new RECT(width - pad - valueWidth, 0, width - pad, height);

        int trackLeft = glyph.Right + Scale(10);
        int trackRight = value.Left - Scale(10);
        int trackTop = (height - trackHeight) / 2;

        track = new RECT(trackLeft, trackTop, trackRight, trackTop + trackHeight);
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

        var edge = new RECT(0, 0, width, 1);
        Win32.FillRect(dc, ref edge, _brushBorder);
        edge = new RECT(0, height - 1, width, height);
        Win32.FillRect(dc, ref edge, _brushBorder);
        edge = new RECT(0, 0, 1, height);
        Win32.FillRect(dc, ref edge, _brushBorder);
        edge = new RECT(width - 1, 0, width, height);
        Win32.FillRect(dc, ref edge, _brushBorder);

        Layout(width, height, out RECT glyph, out RECT track, out RECT value);

        SystemIndicators.CurrentVolume(out float level, out bool muted);
        int percent = (int)MathF.Round(level * 100f);

        IntPtr previousFont = Win32.SelectObject(dc, _fontGlyph);
        DrawLabel(dc, muted ? "" : "", ref glyph,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorText);

        Win32.SelectObject(dc, _fontUi);
        DrawLabel(dc, muted ? "Muted" : $"{percent}%", ref value,
            DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX, ColorText);
        Win32.SelectObject(dc, previousFont);

        Win32.FillRect(dc, ref track, _brushTrack);

        if (!muted && percent > 0)
        {
            int filled = (int)(track.Width * level);
            var fill = new RECT(track.Left, track.Top, track.Left + filled, track.Bottom);
            Win32.FillRect(dc, ref fill, _brushAccent);
        }

        int thumbWidth = Scale(BaseThumbWidth);
        int thumbHeight = Scale(BaseThumbHeight);
        int thumbCenter = track.Left + (int)(track.Width * level);
        var thumb = new RECT(
            thumbCenter - (thumbWidth / 2),
            ((track.Top + track.Bottom) / 2) - (thumbHeight / 2),
            thumbCenter + (thumbWidth / 2),
            ((track.Top + track.Bottom) / 2) + (thumbHeight / 2));

        Win32.FillRect(dc, ref thumb, _brushThumb);
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

        _fontGlyph = Win32.CreateFont(
            -Scale(15), 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe MDL2 Assets");

        _brushBg = Win32.CreateSolidBrush(ColorBg);
        _brushBorder = Win32.CreateSolidBrush(ColorBorder);
        _brushTrack = Win32.CreateSolidBrush(ColorTrack);
        _brushAccent = Win32.CreateSolidBrush(ColorAccent);
        _brushThumb = Win32.CreateSolidBrush(ColorThumb);
    }

    private static void ReleaseResources()
    {
        IntPtr[] handles = [_fontUi, _fontGlyph, _brushBg, _brushBorder, _brushTrack, _brushAccent, _brushThumb];

        foreach (IntPtr handle in handles)
        {
            if (handle != IntPtr.Zero)
                Win32.DeleteObject(handle);
        }

        _fontUi = _fontGlyph = IntPtr.Zero;
        _brushBg = _brushBorder = _brushTrack = _brushAccent = _brushThumb = IntPtr.Zero;

        ReleaseBackBuffer();
    }
}
