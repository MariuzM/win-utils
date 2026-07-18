using System.Runtime.InteropServices;

namespace WinSnip.Native;

// Same rule as WinShell: every struct here is blittable on purpose - no ByValTStr, no bool fields,
// no string fields. LibraryImport will not source-generate marshalling for non-blittable types
// without extra ceremony. Where the real Win32 struct holds a string (WNDCLASSEXW) we keep an
// IntPtr and marshal by hand at the call site; where it holds a fixed character buffer
// (NOTIFYICONDATAW) we use a fixed char[] and copy into it.

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int Cx;
    public int Cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    // Normalised rectangle between two arbitrary corners - the drag anchor is not necessarily the
    // top-left, because the user may drag up and/or left.
    public static RECT FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public IntPtr lpszMenuName;
    public IntPtr lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

// V3 layout (Windows 2000+). cbSize is always set from sizeof(NOTIFYICONDATAW) so the shell can
// tell which version it is being handed.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersionOrTimeout;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

internal static class Win32Const
{
    // ---- Window styles ----------------------------------------------------------

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;

    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_APPWINDOW = 0x00040000;

    public const int GWL_EXSTYLE = -20;

    // ---- Messages ---------------------------------------------------------------

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;

    // Our own tray callback. Anything in the WM_APP range is free for application use.
    public const uint WM_TRAY_CALLBACK = WM_APP + 1;

    // Posted to the message-loop thread when a background capture finishes, so that any UI work
    // (balloon tip, clipboard) happens on the thread that owns the window.
    public const uint WM_CAPTURE_DONE = WM_APP + 2;

    public const int VK_ESCAPE = 0x1B;

    // ---- Hotkeys ----------------------------------------------------------------

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- Show / position --------------------------------------------------------

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // ---- System metrics ---------------------------------------------------------

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // ---- Cursors ----------------------------------------------------------------

    public const int IDC_ARROW = 32512;
    public const int IDC_CROSS = 32515;

    public const uint MB_ICONWARNING = 0x00000030;

    // ---- Layered windows --------------------------------------------------------

    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    // ---- Tray icon --------------------------------------------------------------

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;

    public const uint NOTIFYICON_VERSION_4 = 4;

    // ---- Menus ------------------------------------------------------------------

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    // ---- Window enumeration -----------------------------------------------------

    public const uint GW_OWNER = 4;
    public const uint GA_ROOT = 2;

    public const int GWL_STYLE = -16;
    public const uint WS_MINIMIZE = 0x20000000;

    public const uint DWMWA_CLOAKED = 14;
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
