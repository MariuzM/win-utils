using System.Runtime.InteropServices;

namespace WinBorder;

internal static unsafe partial class Native
{
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateEventW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    public static partial nint CreateEvent(
        nint eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name
    );

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "OpenEventW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    public static partial nint OpenEvent(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name
    );

    [LibraryImport("kernel32.dll", EntryPoint = "SetEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateMutexW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    public static partial nint CreateMutex(
        nint mutexAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool initialOwner,
        string name
    );

    [LibraryImport("kernel32.dll", EntryPoint = "ReleaseMutex")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseMutex(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateWaitableTimerExW",
        SetLastError = true
    )]
    public static partial nint CreateWaitableTimerEx(
        nint timerAttributes,
        nint name,
        uint flags,
        uint desiredAccess
    );

    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(
        nint timer,
        long* dueTime,
        int period,
        nint completionRoutine,
        nint completionArg,
        [MarshalAs(UnmanagedType.Bool)] bool resume
    );

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForMultipleObjects")]
    public static partial uint WaitForMultipleObjects(
        uint count,
        nint* handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds
    );

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint period);

    [LibraryImport("user32.dll", EntryPoint = "SetWinEventHook")]
    public static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback,
        uint processId,
        uint threadId,
        uint flags
    );

    [LibraryImport("user32.dll", EntryPoint = "UnhookWinEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx")]
    public static partial uint MsgWaitForMultipleObjectsEx(
        uint count,
        nint* handles,
        uint milliseconds,
        uint wakeMask,
        uint flags
    );

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(ref Message message);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    public static partial int DwmSetWindowAttribute(nint window, uint attribute, uint* value, uint size);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Message
{
    public nint Window;
    public uint Id;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public Point Point;
    public uint Private;
}
