using System.Runtime.InteropServices;

namespace WinShell.Native;

// Every struct here is blittable on purpose - no ByValTStr, no bool fields, no string fields.
// LibraryImport refuses to source-generate marshalling for non-blittable types without extra
// ceremony, and blittable structs also cost nothing to pass. Where the real Win32 struct has a
// string (WNDCLASSEXW), we hold an IntPtr and marshal the string by hand at the call site.

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

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
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
internal struct APPBARDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
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
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public IntPtr hwndTrack;
    public uint dwHoverTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRAYNOTIFYHEADER
{
    public uint dwSignature;
    public uint dwMessage;
    public uint cbSize;
    public uint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public uint hIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct COPYDATASTRUCT
{
    public IntPtr dwData;
    public uint cbData;
    public IntPtr lpData;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SHFILEINFOW
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    public fixed char szDisplayName[260];
    public fixed char szTypeName[80];
}

[StructLayout(LayoutKind.Sequential)]
internal struct GdiplusStartupInput
{
    public uint GdiplusVersion;
    public IntPtr DebugEventCallback;
    public int SuppressBackgroundThread;
    public int SuppressExternalCodecs;
}

// Passed by value to AlphaBlend. Four bytes, so it goes in a register - no marshalling.
[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPOS
{
    public IntPtr hwnd;
    public IntPtr hwndInsertAfter;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public uint flags;
}

/// <summary>
/// pFrom and pTo are double-null-terminated lists, not plain strings, which is why they are
/// held as raw pointers and built by hand at the call site.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SHFILEOPSTRUCTW
{
    public IntPtr hwnd;
    public uint wFunc;
    public IntPtr pFrom;
    public IntPtr pTo;
    public ushort fFlags;
    public int fAnyOperationsAborted;
    public IntPtr hNameMappings;
    public IntPtr lpszProgressTitle;
}

/// <summary>
/// The non-Ex form is enough here: verbs are passed as a command offset through
/// MAKEINTRESOURCE rather than as strings, which is what IContextMenu expects back from
/// TrackPopupMenuEx.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CMINVOKECOMMANDINFO
{
    public uint cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon;
}

internal static class Win32Const
{
    // Window styles
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;

    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_APPWINDOW = 0x00040000;

    // Messages
    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_GETICON = 0x007F;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_DPICHANGED = 0x02E0;

    // ShowWindow
    public const int SW_HIDE = 0;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;

    // GetWindowLongPtr
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GW_OWNER = 4;

    // Class longs
    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    // WM_GETICON
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // AppBar
    public const uint ABM_NEW = 0x0000;
    public const uint ABM_REMOVE = 0x0001;
    public const uint ABM_QUERYPOS = 0x0002;
    public const uint ABM_SETPOS = 0x0003;
    public const uint ABM_WINDOWPOSCHANGED = 0x0009;
    public const uint ABE_BOTTOM = 3;
    public const int ABN_POSCHANGED = 1;
    public const int ABN_FULLSCREENAPP = 2;

    // SetWindowPos
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // DWM - DWMWA_CLOAKED is how Windows 11 marks suspended UWP windows that must not
    // appear in a task list even though IsWindowVisible still reports true for them.
    public const uint DWMWA_CLOAKED = 14;

    // WinEvent hooks
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    // Drawing
    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020;
    public const uint DI_NORMAL = 0x0003;
    public const uint DT_LEFT = 0x0000;
    public const uint DT_CENTER = 0x0001;
    public const uint DT_VCENTER = 0x0004;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_NOPREFIX = 0x0800;
    public const uint DT_END_ELLIPSIS = 0x8000;
    public const uint DT_WORDBREAK = 0x0010;

    public const uint TME_LEAVE = 0x00000002;

    public const int IDC_ARROW = 32512;

    public const uint CS_GLOBALCLASS = 0x4000;

    public const uint WM_COPYDATA = 0x004A;
    public const uint TRAY_SIGNATURE = 0x34753423;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint WS_CHILD = 0x40000000;

    // Shell_NotifyIcon
    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const int TRAY_TIP_OFFSET = 32;
    public const int TRAY_TIP_CHARS = 128;

    // GetSystemMetrics
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPIF_SENDCHANGE = 0x02;

    // Start menu input
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const int WA_INACTIVE = 0;

    public const int VK_BACK = 0x08;
    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_PRIOR = 0x21;
    public const int VK_NEXT = 0x22;
    public const int VK_UP = 0x26;
    public const int VK_DOWN = 0x28;

    // SHGetFileInfo
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_PIDL = 0x000000008;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    // IShellItem::GetDisplayName
    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint CLSCTX_ALL = 0x17;

    // IApplicationActivationManager::ActivateApplication
    public const uint AO_NONE = 0x0;

    // ExitWindowsEx
    public const uint EWX_LOGOFF = 0x00000000;

    // ---- Desktop -----------------------------------------------------------------------

    public const uint WM_SIZE = 0x0005;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_INITMENUPOPUP = 0x0117;
    public const uint WM_MENUCHAR = 0x0120;
    public const uint WM_MEASUREITEM = 0x002C;
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_DROPFILES = 0x0233;
    public const uint EM_SETSEL = 0x00B1;

    // CS_DBLCLKS is what makes the class generate WM_LBUTTONDBLCLK at all - without it a
    // double click arrives as two unrelated WM_LBUTTONDOWNs and nothing ever opens.
    public const uint CS_DBLCLKS = 0x0008;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint MK_SHIFT = 0x0004;
    public const uint MK_CONTROL = 0x0008;

    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_END = 0x23;
    public const int VK_HOME = 0x24;
    public const int VK_LEFT = 0x25;
    public const int VK_RIGHT = 0x27;
    public const int VK_DELETE = 0x2E;
    public const int VK_A = 0x41;
    public const int VK_F2 = 0x71;
    public const int VK_F5 = 0x74;

    // Stretch modes. HALFTONE is the only one that resamples rather than dropping rows, which
    // matters a great deal when a 4K wallpaper is scaled down to fit.
    public const int COLORONCOLOR = 3;
    public const int HALFTONE = 4;

    public const uint DT_CALCRECT = 0x0400;
    public const uint DT_NOCLIP = 0x0100;
    public const uint DT_EDITCONTROL = 0x2000;

    // AlphaBlend
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    // TrackPopupMenuEx
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;

    // IContextMenu::QueryContextMenu
    public const uint CMF_NORMAL = 0x00000000;
    public const uint CMF_EXPLORE = 0x00000004;
    public const uint CMF_CANRENAME = 0x00000010;

    // SHFileOperation. FOF_ALLOWUNDO is what sends a delete to the Recycle Bin instead of
    // destroying the file outright.
    public const uint FO_MOVE = 0x0001;
    public const uint FO_COPY = 0x0002;
    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;
    public const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    // System image lists. EXTRALARGE is 48px, which is the desktop's default icon size.
    public const int SHIL_LARGE = 0;
    public const int SHIL_EXTRALARGE = 2;
    public const int SHIL_JUMBO = 4;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;
    public const uint ILD_TRANSPARENT = 0x00000001;

    public const int GWLP_WNDPROC = -4;
    public const uint ES_AUTOHSCROLL = 0x0080;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_TABSTOP = 0x00010000;

    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const uint SPI_GETWORKAREA = 0x0030;
}
