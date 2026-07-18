using System.Runtime.InteropServices;

namespace WinShell.Native;

// [LibraryImport] rather than [DllImport] throughout: the marshalling stubs are generated at
// compile time instead of emitted by the runtime, which is what makes this assembly AOT-safe.
// Buffers are passed as raw char* with stackalloc at the call site so nothing allocates on the
// paint or window-enumeration paths.
internal static unsafe partial class Win32
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Dwmapi = "dwmapi.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Ole32 = "ole32.dll";
    private const string GdiPlus = "gdiplus.dll";
    private const string MsImg32 = "msimg32.dll";

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

    [LibraryImport(User32, EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport(User32, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "SendMessageTimeoutW")]
    public static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [LibraryImport(User32, EntryPoint = "SetTimer")]
    public static partial IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport(User32, EntryPoint = "KillTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, IntPtr nIDEvent);

    // ---- Window query & manipulation --------------------------------------------

    [LibraryImport(User32, EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport(User32, EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport(User32, EntryPoint = "ShowWindowAsync")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [LibraryImport(User32, EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "GetForegroundWindow")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport(User32, EntryPoint = "GetWindow")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport(User32, EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport(User32, EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport(User32, EntryPoint = "GetClassLongPtrW")]
    public static partial IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport(User32, EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport(User32, EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport(User32, EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport(User32, EntryPoint = "InvalidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport(User32, EntryPoint = "SetCapture")]
    public static partial IntPtr SetCapture(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport(User32, EntryPoint = "TrackMouseEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [LibraryImport(User32, EntryPoint = "GetWindowThreadProcessId")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport(User32, EntryPoint = "keybd_event")]
    public static partial void KeybdEvent(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // ---- Monitors & DPI ----------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport(User32, EntryPoint = "MonitorFromWindow")]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport(User32, EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport(User32, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    // ---- Painting ----------------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "BeginPaint")]
    public static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [LibraryImport(User32, EntryPoint = "EndPaint")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [LibraryImport(User32, EntryPoint = "FillRect")]
    public static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [LibraryImport(User32, EntryPoint = "DrawTextW")]
    public static partial int DrawText(IntPtr hdc, char* lpchText, int cchText, ref RECT lprc, uint format);

    [LibraryImport(User32, EntryPoint = "DrawIconEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [LibraryImport(User32, EntryPoint = "CopyIcon")]
    public static partial IntPtr CopyIcon(IntPtr hIcon);

    [LibraryImport(User32, EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    // ---- GDI ---------------------------------------------------------------------

    [LibraryImport(Gdi32, EntryPoint = "CreateCompatibleDC")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "CreateCompatibleBitmap")]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport(Gdi32, EntryPoint = "SelectObject")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport(Gdi32, EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport(Gdi32, EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport(Gdi32, EntryPoint = "BitBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(
        IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [LibraryImport(Gdi32, EntryPoint = "CreateSolidBrush")]
    public static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport(Gdi32, EntryPoint = "CreatePen")]
    public static partial IntPtr CreatePen(int iStyle, int cWidth, uint color);

    [LibraryImport(Gdi32, EntryPoint = "SetBkMode")]
    public static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport(Gdi32, EntryPoint = "SetTextColor")]
    public static partial uint SetTextColor(IntPtr hdc, uint color);

    [LibraryImport(Gdi32, EntryPoint = "MoveToEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [LibraryImport(Gdi32, EntryPoint = "LineTo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LineTo(IntPtr hdc, int x, int y);

    [LibraryImport(Gdi32, EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFont(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    // ---- Shell / appbar -----------------------------------------------------------

    [LibraryImport(Shell32, EntryPoint = "SHAppBarMessage")]
    public static partial IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [LibraryImport(Shell32, EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ShellExecute(
        IntPtr hwnd, string? lpOperation, string lpFile, string? lpParameters,
        string? lpDirectory, int nShowCmd);

    [LibraryImport(Shell32, EntryPoint = "SHGetKnownFolderItem")]
    public static partial int SHGetKnownFolderItem(
        Guid* rfid, uint dwFlags, IntPtr hToken, Guid* riid, IntPtr* ppv);

    [LibraryImport(Shell32, EntryPoint = "SHGetIDListFromObject")]
    public static partial int SHGetIDListFromObject(IntPtr punk, IntPtr* ppidl);

    [LibraryImport(Shell32, EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SHGetFileInfoPath(
        string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [LibraryImport(Shell32, EntryPoint = "SHGetFileInfoW")]
    public static partial IntPtr SHGetFileInfoPidl(
        IntPtr pidl, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    // ---- Session ---------------------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "LockWorkStation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    [LibraryImport(User32, EntryPoint = "ExitWindowsEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ExitWindowsEx(uint uFlags, uint dwReason);

    // ---- COM -------------------------------------------------------------------------

    [LibraryImport(Ole32, EntryPoint = "CoInitializeEx")]
    public static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport(Ole32, EntryPoint = "CoUninitialize")]
    public static partial void CoUninitialize();

    [LibraryImport(Ole32, EntryPoint = "CoTaskMemFree")]
    public static partial void CoTaskMemFree(IntPtr pv);

    [LibraryImport(Ole32, EntryPoint = "CoCreateInstance")]
    public static partial int CoCreateInstance(
        Guid* rclsid, IntPtr pUnkOuter, uint dwClsContext, Guid* riid, IntPtr* ppv);

    // Hands our foreground right to an out-of-process COM server, so an app it activates on
    // our behalf can come to the front instead of opening behind the caller.
    [LibraryImport(Ole32, EntryPoint = "CoAllowSetForegroundWindow")]
    public static partial int CoAllowSetForegroundWindow(IntPtr pUnk, IntPtr lpvReserved);

    // ---- Indicators -------------------------------------------------------------------

    [LibraryImport(Kernel32, EntryPoint = "GetSystemPowerStatus")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [LibraryImport("wininet.dll", EntryPoint = "InternetGetConnectedState")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InternetGetConnectedState(out uint lpdwFlags, uint dwReserved);

    // ---- DWM ----------------------------------------------------------------------

    [LibraryImport(Dwmapi, EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    // ---- WinEvent hooks -------------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "SetWinEventHook")]
    public static partial IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        delegate* unmanaged<IntPtr, uint, IntPtr, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport(User32, EntryPoint = "UnhookWinEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---- GDI+ ------------------------------------------------------------------------
    // Only used to decode the wallpaper. GDI itself can load nothing but BMP, and wallpapers
    // are JPEG or PNG in practice, so something has to decode them. GDI+ is the flat C API
    // here (not System.Drawing), which keeps this AOT-clean and costs one DLL at startup.

    [LibraryImport(GdiPlus, EntryPoint = "GdiplusStartup")]
    public static partial int GdiplusStartup(IntPtr* token, GdiplusStartupInput* input, IntPtr output);

    [LibraryImport(GdiPlus, EntryPoint = "GdiplusShutdown")]
    public static partial void GdiplusShutdown(IntPtr token);

    [LibraryImport(GdiPlus, EntryPoint = "GdipCreateBitmapFromFile")]
    public static partial int GdipCreateBitmapFromFile(char* filename, IntPtr* bitmap);

    [LibraryImport(GdiPlus, EntryPoint = "GdipCreateHBITMAPFromBitmap")]
    public static partial int GdipCreateHBITMAPFromBitmap(IntPtr bitmap, IntPtr* hbmReturn, uint background);

    [LibraryImport(GdiPlus, EntryPoint = "GdipGetImageWidth")]
    public static partial int GdipGetImageWidth(IntPtr image, uint* width);

    [LibraryImport(GdiPlus, EntryPoint = "GdipGetImageHeight")]
    public static partial int GdipGetImageHeight(IntPtr image, uint* height);

    [LibraryImport(GdiPlus, EntryPoint = "GdipDisposeImage")]
    public static partial int GdipDisposeImage(IntPtr image);

    // ---- GDI, desktop painting ---------------------------------------------------------

    [LibraryImport(Gdi32, EntryPoint = "StretchBlt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StretchBlt(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [LibraryImport(Gdi32, EntryPoint = "SetStretchBltMode")]
    public static partial int SetStretchBltMode(IntPtr hdc, int mode);

    [LibraryImport(Gdi32, EntryPoint = "SetBrushOrgEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [LibraryImport(Gdi32, EntryPoint = "RoundRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int cw, int ch);

    [LibraryImport(Gdi32, EntryPoint = "GetStockObject")]
    public static partial IntPtr GetStockObject(int i);

    // Used for the selection wash and the rubber band. Drawing those opaque looks wrong; a
    // real alpha blend is the difference between "a selection" and "a blue box".
    [LibraryImport(MsImg32, EntryPoint = "AlphaBlend")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AlphaBlend(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, BLENDFUNCTION blend);

    // ---- Shell namespace, icons & context menus -----------------------------------------

    // The system image lists. SHGFI_ICON tops out at 32px; desktop icons are 48px, so the
    // large sizes have to come from the image list directly.
    [LibraryImport(Shell32, EntryPoint = "SHGetImageList")]
    public static partial int SHGetImageList(int iImageList, Guid* riid, IntPtr* ppv);

    [LibraryImport(Shell32, EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(
        string pszName, IntPtr pbc, IntPtr* ppidl, uint sfgaoIn, uint* psfgaoOut);

    [LibraryImport(Shell32, EntryPoint = "SHBindToParent")]
    public static partial int SHBindToParent(IntPtr pidl, Guid* riid, IntPtr* ppv, IntPtr* ppidlLast);

    [LibraryImport(Shell32, EntryPoint = "SHBindToObject")]
    public static partial int SHBindToObject(IntPtr psf, IntPtr pidl, IntPtr pbc, Guid* riid, IntPtr* ppv);

    [LibraryImport(Shell32, EntryPoint = "ILFree")]
    public static partial void ILFree(IntPtr pidl);

    // Returns a pointer *into* the given PIDL, not a copy - the result must not be freed and
    // stays valid only as long as the PIDL it points into does.
    [LibraryImport(Shell32, EntryPoint = "ILFindLastID")]
    public static partial IntPtr ILFindLastID(IntPtr pidl);

    [LibraryImport(Shell32, EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(SHFILEOPSTRUCTW* lpFileOp);

    [LibraryImport(Shell32, EntryPoint = "DragAcceptFiles")]
    public static partial void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

    [LibraryImport(Shell32, EntryPoint = "DragQueryFileW")]
    public static partial uint DragQueryFile(IntPtr hDrop, uint iFile, char* lpszFile, uint cch);

    [LibraryImport(Shell32, EntryPoint = "DragFinish")]
    public static partial void DragFinish(IntPtr hDrop);

    // ---- Menus ----------------------------------------------------------------------------

    [LibraryImport(User32, EntryPoint = "CreatePopupMenu")]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport(User32, EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport(User32, EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [LibraryImport(User32, EntryPoint = "GetMenuItemCount")]
    public static partial int GetMenuItemCount(IntPtr hMenu);

    [LibraryImport(User32, EntryPoint = "TrackPopupMenuEx")]
    public static partial int TrackPopupMenuEx(
        IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    // ---- Child controls (the in-place rename box) -------------------------------------------

    [LibraryImport(User32, EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(IntPtr hWnd, string lpString);

    [LibraryImport(User32, EntryPoint = "SetFocus")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport(User32, EntryPoint = "CallWindowProcW")]
    public static partial IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, EntryPoint = "GetKeyState")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport(User32, EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    // ---- Helpers ---------------------------------------------------------------------

    /// <summary>Packs an RGB triplet into the COLORREF (0x00BBGGRR) layout GDI expects.</summary>
    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public static int LoWord(IntPtr value) => unchecked((short)(long)value);

    public static int HiWord(IntPtr value) => unchecked((short)((long)value >> 16));

    /// <summary>
    /// Reads a window title without allocating a managed buffer per call. Returns an empty
    /// string for untitled windows so callers can filter on length.
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        const int max = 256;
        char* buffer = stackalloc char[max];
        int length = GetWindowText(hWnd, buffer, max);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public static string GetWindowClass(IntPtr hWnd)
    {
        const int max = 256;
        char* buffer = stackalloc char[max];
        int length = GetClassName(hWnd, buffer, max);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
