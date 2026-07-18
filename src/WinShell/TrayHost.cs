using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal sealed class TrayIcon
{
    public uint OwnerHwnd;
    public uint Id;
    public uint CallbackMessage;
    public IntPtr Icon;
    public string Tip = string.Empty;
    public RECT Bounds;
}

internal static unsafe class TrayHost
{
    private const string TrayClass = "Shell_TrayWnd";
    private const string NotifyClass = "TrayNotifyWnd";

    private static IntPtr _hwnd;
    private static IntPtr _notifyHwnd;
    private static uint _taskbarCreated;

    private static readonly List<TrayIcon> IconList = new();

    public static IReadOnlyList<TrayIcon> Icons => IconList;

    public static bool Active => _hwnd != IntPtr.Zero;

    public static bool Create()
    {
        IntPtr instance = Win32.GetModuleHandle(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = CS_GLOBALCLASS,
            lpfnWndProc = &WndProc,
            hInstance = instance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni(TrayClass),
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            return false;

        _hwnd = Win32.CreateWindowEx(
            WS_EX_TOOLWINDOW, TrayClass, string.Empty, WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            return false;

        var nc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = CS_GLOBALCLASS,
            lpfnWndProc = &WndProc,
            hInstance = instance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni(NotifyClass),
        };

        Win32.RegisterClassEx(ref nc);
        _notifyHwnd = Win32.CreateWindowEx(
            0, NotifyClass, string.Empty, WS_CHILD,
            0, 0, 0, 0, _hwnd, IntPtr.Zero, instance, IntPtr.Zero);

        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        Win32.PostMessage(HWND_BROADCAST, _taskbarCreated, IntPtr.Zero, IntPtr.Zero);

        return true;
    }

    public static void Destroy()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        foreach (TrayIcon icon in IconList)
        {
            if (icon.Icon != IntPtr.Zero)
                Win32.DestroyIcon(icon.Icon);
        }

        IconList.Clear();

        if (_notifyHwnd != IntPtr.Zero)
            Win32.DestroyWindow(_notifyHwnd);

        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _notifyHwnd = IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_COPYDATA && OnCopyData(lParam))
            {
                TaskbarWindow.Invalidate();
                return new IntPtr(1);
            }
        }
        catch
        {
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static bool OnCopyData(IntPtr lParam)
    {
        var cds = *(COPYDATASTRUCT*)lParam;

        if ((long)cds.dwData != 1 || cds.lpData == IntPtr.Zero
            || cds.cbData < (uint)sizeof(TRAYNOTIFYHEADER))
        {
            return false;
        }

        var header = *(TRAYNOTIFYHEADER*)cds.lpData;

        if (header.dwSignature != TRAY_SIGNATURE)
            return false;

        switch (header.dwMessage)
        {
            case NIM_ADD:
            case NIM_MODIFY:
                Upsert(header, cds);
                return true;

            case NIM_DELETE:
                Remove(header.hWnd, header.uID);
                return true;

            default:
                return false;
        }
    }

    private static void Upsert(TRAYNOTIFYHEADER header, COPYDATASTRUCT cds)
    {
        if (header.hWnd == 0)
            return;

        TrayIcon? existing = Find(header.hWnd, header.uID);

        if (existing == null)
        {
            existing = new TrayIcon { OwnerHwnd = header.hWnd, Id = header.uID };
            IconList.Add(existing);
        }

        if ((header.uFlags & NIF_MESSAGE) != 0)
            existing.CallbackMessage = header.uCallbackMessage;

        if ((header.uFlags & NIF_ICON) != 0)
        {
            IntPtr copy = header.hIcon == 0
                ? IntPtr.Zero
                : Win32.CopyIcon(new IntPtr(header.hIcon));

            if (existing.Icon != IntPtr.Zero)
                Win32.DestroyIcon(existing.Icon);

            existing.Icon = copy;
        }

        if ((header.uFlags & NIF_TIP) != 0)
            existing.Tip = ReadTip(cds);
    }

    private static string ReadTip(COPYDATASTRUCT cds)
    {
        if (cds.cbData < TRAY_TIP_OFFSET + (TRAY_TIP_CHARS * sizeof(char)))
            return string.Empty;

        char* start = (char*)((byte*)cds.lpData + TRAY_TIP_OFFSET);

        int length = 0;
        while (length < TRAY_TIP_CHARS && start[length] != '\0')
            length++;

        return length > 0 ? new string(start, 0, length) : string.Empty;
    }

    private static TrayIcon? Find(uint hwnd, uint id)
    {
        foreach (TrayIcon icon in IconList)
        {
            if (icon.OwnerHwnd == hwnd && icon.Id == id)
                return icon;
        }

        return null;
    }

    private static void Remove(uint hwnd, uint id)
    {
        for (int i = 0; i < IconList.Count; i++)
        {
            if (IconList[i].OwnerHwnd != hwnd || IconList[i].Id != id)
                continue;

            if (IconList[i].Icon != IntPtr.Zero)
                Win32.DestroyIcon(IconList[i].Icon);

            IconList.RemoveAt(i);
            return;
        }
    }

    public static bool PruneDead()
    {
        bool changed = false;

        for (int i = IconList.Count - 1; i >= 0; i--)
        {
            if (Win32.IsWindow(new IntPtr(IconList[i].OwnerHwnd)))
                continue;

            if (IconList[i].Icon != IntPtr.Zero)
                Win32.DestroyIcon(IconList[i].Icon);

            IconList.RemoveAt(i);
            changed = true;
        }

        return changed;
    }

    public static void Click(TrayIcon icon, uint mouseMessage)
    {
        if (icon.CallbackMessage == 0)
            return;

        var owner = new IntPtr(icon.OwnerHwnd);
        if (!Win32.IsWindow(owner))
            return;

        Win32.SetForegroundWindow(owner);
        Win32.PostMessage(owner, icon.CallbackMessage, new IntPtr(icon.Id), new IntPtr(mouseMessage));
    }
}
