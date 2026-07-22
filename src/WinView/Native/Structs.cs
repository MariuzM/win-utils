using System.Runtime.InteropServices;

namespace WinView.Native;

// Blittable only: LibraryImport will not source-generate marshalling for non-blittable types
// without extra ceremony.

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
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
internal unsafe struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public fixed byte rgbReserved[32];
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
internal struct GdiplusStartupInput
{
    public uint GdiplusVersion;
    public IntPtr DebugEventCallback;
    public int SuppressBackgroundThread;
    public int SuppressExternalCodecs;
}

// GDI+ PropertyItem. On 64-bit the compiler's natural alignment matches the native layout: id and
// length at 0 and 4, type at 8, six bytes of padding, then the pointer at 16.
//
// GdipGetPropertyItem writes both this header and the value bytes into one caller-supplied buffer,
// so `value` points inside that same allocation rather than to memory GDI+ owns.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PropertyItem
{
    public uint id;
    public uint length;
    public ushort type;
    public byte* value;
}

internal static class Win32Const
{
    // ---- Window styles ----------------------------------------------------------

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_EX_ACCEPTFILES = 0x00000010;

    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    // ---- Messages ---------------------------------------------------------------

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_DROPFILES = 0x0233;
    public const uint WM_DPICHANGED = 0x02E0;

    // ---- Virtual keys -----------------------------------------------------------

    public const int VK_ESCAPE = 0x1B;
    public const int VK_PRIOR = 0x21;
    public const int VK_NEXT = 0x22;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_OEM_PLUS = 0xBB;
    public const int VK_OEM_MINUS = 0xBD;
    public const int VK_ADD = 0x6B;
    public const int VK_SUBTRACT = 0x6D;
    public const int VK_0 = 0x30;
    public const int VK_1 = 0x31;

    // ---- Show / position --------------------------------------------------------

    public const int SW_SHOW = 5;
    public const int SW_SHOWMAXIMIZED = 3;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int IDC_ARROW = 32512;
    public const int IDC_SIZEALL = 32646;

    // ---- GDI+ -------------------------------------------------------------------

    // Bicubic when shrinking keeps detail; nearest-neighbour above 1:1 is deliberate, so zooming
    // into a screenshot shows crisp pixels rather than a blurred interpolation.
    public const int InterpolationModeNearestNeighbor = 5;
    public const int InterpolationModeHighQualityBicubic = 7;

    public const int PixelOffsetModeHighQuality = 4;

    public const int SmoothingModeNone = 3;

    // EXIF orientation tag.
    public const uint PropertyTagOrientation = 0x0112;

    public const int RotateNoneFlipNone = 0;
    public const int Rotate90FlipNone = 1;
    public const int Rotate180FlipNone = 2;
    public const int Rotate270FlipNone = 3;
    public const int RotateNoneFlipX = 4;
    public const int Rotate90FlipX = 5;
    public const int Rotate180FlipX = 6;
    public const int Rotate270FlipX = 7;
}
