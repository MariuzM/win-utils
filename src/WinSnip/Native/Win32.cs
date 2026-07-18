using System.Runtime.InteropServices;

namespace WinSnip.Native;

// [LibraryImport] throughout, matching WinShell: marshalling stubs are generated at compile time
// rather than emitted by the runtime. Callbacks are function pointers, never delegates, so nothing
// has to be kept alive against the GC across a native call.
internal static unsafe partial class Win32
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Dwmapi = "dwmapi.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string D3D11 = "d3d11.dll";
    private const string CombaseDll = "combase.dll";

    // ---- Module ----------------------------------------------------------------

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);

    [LibraryImport(Kernel32, EntryPoint = "AttachConsole")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    // ---- Window class & creation ------------------------------------------------

    [LibraryImport(User32, EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(ref WNDCLASSEXW lpwcx);

    [LibraryImport(User32, EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport(User32, EntryPoint = "SetCursor")]
    public static partial IntPtr SetCursor(IntPtr hCursor);

    [LibraryImport(User32, EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport(User32, EntryPoint = "LoadIconW")]
    public static partial IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    // ---- Message loop -----------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(User32, EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport(User32, EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(ref MSG lpMsg);

    [LibraryImport(User32, EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(User32, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- Window state -----------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport(User32, EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport(User32, EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport(User32, EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport(User32, EntryPoint = "GetWindow")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport(User32, EntryPoint = "GetAncestor")]
    public static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport(User32, EntryPoint = "WindowFromPoint")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport(User32, EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport(User32, EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport(User32, EntryPoint = "GetWindowThreadProcessId")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- Mouse & screen ---------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport(User32, EntryPoint = "SetCapture")]
    public static partial IntPtr SetCapture(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport(User32, EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport(User32, EntryPoint = "MonitorFromPoint")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport(User32, EntryPoint = "MonitorFromWindow")]
    public static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---- Hotkeys ----------------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "RegisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(User32, EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- Tray & menus -----------------------------------------------------------

    [LibraryImport(Shell32, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [LibraryImport(User32, EntryPoint = "CreatePopupMenu")]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport(User32, EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [LibraryImport(User32, EntryPoint = "TrackPopupMenuEx")]
    public static partial int TrackPopupMenuEx(
        IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [LibraryImport(User32, EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    // ---- DWM --------------------------------------------------------------------

    [LibraryImport(Dwmapi, EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    // Second overload for DWMWA_EXTENDED_FRAME_BOUNDS. GetWindowRect on Win10/11 includes the
    // invisible resize border, which would put a band of desktop around every window capture.
    [LibraryImport(Dwmapi, EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttributeRect(IntPtr hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);

    // ---- GDI (layered overlay) --------------------------------------------------

    [LibraryImport(User32, EntryPoint = "GetDC")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "ReleaseDC")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport(Gdi32, EntryPoint = "CreateCompatibleDC")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "CreateDIBSection")]
    public static partial IntPtr CreateDIBSection(
        IntPtr hdc, BITMAPINFOHEADER* pbmi, uint usage, out void* ppvBits, IntPtr hSection, uint offset);

    [LibraryImport(Gdi32, EntryPoint = "SelectObject")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport(Gdi32, EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport(User32, EntryPoint = "UpdateLayeredWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, POINT* pptDst, SIZE* psize, IntPtr hdcSrc, POINT* pptSrc,
        uint crKey, BLENDFUNCTION* pblend, uint dwFlags);

    // ---- Direct3D 11 ------------------------------------------------------------
    //
    // Only two flat exports and one QueryInterface are needed. The device is created purely to be
    // handed to Direct3D11CaptureFramePool - no ID3D11Device or ID3D11DeviceContext method is ever
    // called, which is deliberate: their vtable slot indices are not published in vtable order
    // anywhere authoritative, and a wrong index is a silent crash rather than a compile error.
    // SoftwareBitmap.CreateCopyFromSurfaceAsync does the GPU-to-CPU copy instead.

    [LibraryImport(D3D11, EntryPoint = "D3D11CreateDevice")]
    public static partial int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr ppImmediateContext);

    // Despite living in a header named windows.graphics.directx.direct3d11.interop.h, this export
    // ships in d3d11.dll.
    [LibraryImport(D3D11, EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // ---- WinRT activation -------------------------------------------------------

    [LibraryImport(CombaseDll, EntryPoint = "WindowsCreateString")]
    public static partial int WindowsCreateString(char* sourceString, uint length, out IntPtr hstring);

    [LibraryImport(CombaseDll, EntryPoint = "WindowsDeleteString")]
    public static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport(CombaseDll, EntryPoint = "RoGetActivationFactory")]
    public static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    // ---- Helpers ----------------------------------------------------------------

    public static int LoWord(IntPtr value) => (short)((long)value & 0xFFFF);

    public static int HiWord(IntPtr value) => (short)(((long)value >> 16) & 0xFFFF);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetWindowText(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassName(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
