using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinSnip.Capture;
using WinSnip.Native;
using static WinSnip.Native.Win32Const;

namespace WinSnip.Ui;

// The resident half of WinSnip: a hidden window that owns the tray icon and the global hotkeys, and
// turns either of them into a capture.
// Not unsafe at class level: that makes every member an unsafe context, where await is illegal.
internal static class App
{
    private const string ClassName = "WinSnip.Host";
    private const string SettingsKeyPath = @"Software\WinSnip";
    private const string WindowShadowValue = "IncludeWindowShadow";
    private const uint TrayId = 1;

    private const int HotkeyFullScreen = 1;
    private const int HotkeyRegion = 2;
    private const int HotkeyWindow = 3;

    private const int MenuFullScreen = 10;
    private const int MenuRegion = 11;
    private const int MenuWindow = 12;
    private const int MenuWindowShadow = 13;
    private const int MenuExit = 14;

    private const int VK_1 = 0x31;
    private const int VK_2 = 0x32;
    private const int VK_3 = 0x33;

    // After the overlay closes, DWM needs a moment to compose the frame without it. Capturing
    // immediately can catch the dimmed overlay still on screen.
    private const int OverlaySettleMs = 80;

    private const int ActivationSettleMs = 150;
    private const int ActivationPollMs = 15;
    private const int ActivationPollLimit = 20;

    private static IntPtr _hwnd;
    private static string _lastMessage = string.Empty;
    private static bool _lastFailed;
    private static bool _includeWindowShadow;

    public static int Run()
    {
        _includeWindowShadow = LoadWindowShadowSetting();

        if (!CaptureEngine.IsSupported())
        {
            Fatal("Windows.Graphics.Capture is not supported on this device.");
            return 1;
        }

        if (!Create())
        {
            Fatal("Could not create the WinSnip host window.");
            return 1;
        }

        try
        {
            RegisterHotkeys();
            AddTrayIcon();
            RunMessageLoop();
            return 0;
        }
        finally
        {
            RemoveTrayIcon();
            UnregisterHotkeys();
        }
    }

    private static unsafe bool Create()
    {
        IntPtr namePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0,
            lpfnWndProc = &WndProc,
            hInstance = Win32.GetModuleHandle(null),
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = IntPtr.Zero,
            lpszClassName = namePtr,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
            return false;

        // Never shown. It exists to receive WM_HOTKEY and the tray callback, both of which need a
        // window to be delivered to.
        _hwnd = Win32.CreateWindowEx(
            WS_EX_TOOLWINDOW,
            ClassName,
            "WinSnip",
            WS_POPUP,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32.GetModuleHandle(null),
            IntPtr.Zero
        );

        return _hwnd != IntPtr.Zero;
    }

    private static void RegisterHotkeys()
    {
        // MOD_NOREPEAT stops a held-down chord from queueing a burst of captures.
        const uint mods = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;

        if (!Win32.RegisterHotKey(_hwnd, HotkeyFullScreen, mods, VK_1))
            Report("Ctrl+Shift+1 is already taken by another application.");

        if (!Win32.RegisterHotKey(_hwnd, HotkeyRegion, mods, VK_2))
            Report("Ctrl+Shift+2 is already taken by another application.");

        if (!Win32.RegisterHotKey(_hwnd, HotkeyWindow, mods, VK_3))
            Report("Ctrl+Shift+3 is already taken by another application.");
    }

    private static void UnregisterHotkeys()
    {
        Win32.UnregisterHotKey(_hwnd, HotkeyFullScreen);
        Win32.UnregisterHotKey(_hwnd, HotkeyRegion);
        Win32.UnregisterHotKey(_hwnd, HotkeyWindow);
    }

    // ---- Tray -------------------------------------------------------------------

    private static void AddTrayIcon()
    {
        NOTIFYICONDATAW data = NewIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.uCallbackMessage = WM_TRAY_CALLBACK;

        // IDI_APPLICATION: the project embeds no icon resource, matching WinShell's habit of
        // borrowing system icons rather than shipping assets.
        data.hIcon = Win32.LoadIcon(IntPtr.Zero, new IntPtr(32512));
        SetTip(ref data, "WinSnip - Ctrl+Shift+1/2/3");

        Win32.Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private static void RemoveTrayIcon()
    {
        NOTIFYICONDATAW data = NewIconData(0);
        Win32.Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private static void Notify(string title, string message)
    {
        NOTIFYICONDATAW data = NewIconData(NIF_INFO);
        SetInfo(ref data, title, message);
        Win32.Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private static unsafe NOTIFYICONDATAW NewIconData(uint flags) =>
        new()
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = flags,
        };

    private static unsafe void SetTip(ref NOTIFYICONDATAW data, string tip)
    {
        fixed (char* target = data.szTip)
            CopyString(target, 128, tip);
    }

    private static unsafe void SetInfo(ref NOTIFYICONDATAW data, string title, string message)
    {
        fixed (char* target = data.szInfoTitle)
            CopyString(target, 64, title);

        fixed (char* target = data.szInfo)
            CopyString(target, 256, message);
    }

    private static unsafe void CopyString(char* destination, int capacity, string value)
    {
        int length = Math.Min(value.Length, capacity - 1);
        for (int i = 0; i < length; i++)
            destination[i] = value[i];

        destination[length] = '\0';
    }

    private static void ShowMenu()
    {
        IntPtr menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(MenuFullScreen), "Capture screen\tCtrl+Shift+1");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(MenuRegion), "Capture region\tCtrl+Shift+2");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(MenuWindow), "Capture window\tCtrl+Shift+3");
            Win32.AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            Win32.AppendMenu(
                menu,
                MF_STRING | (_includeWindowShadow ? MF_CHECKED : MF_UNCHECKED),
                new IntPtr(MenuWindowShadow),
                "Include window shadow"
            );
            Win32.AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(MenuExit), "Exit");

            Win32.GetCursorPos(out POINT cursor);

            // Documented requirement: without this the menu does not dismiss when the user clicks
            // away from it, because the owner window is not foreground.
            Win32.SetForegroundWindow(_hwnd);

            int command = Win32.TrackPopupMenuEx(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                cursor.X,
                cursor.Y,
                _hwnd,
                IntPtr.Zero
            );

            Dispatch(command);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    // ---- Capture dispatch -------------------------------------------------------

    private static void Dispatch(int command)
    {
        switch (command)
        {
            case MenuFullScreen:
                RunCapture(Snip.FullScreenAsync);
                break;

            case MenuRegion:
                BeginRegion();
                break;

            case MenuWindow:
                BeginWindowPick();
                break;

            case MenuWindowShadow:
                ToggleWindowShadow();
                break;

            case MenuExit:
                Win32.PostQuitMessage(0);
                break;
        }
    }

    private static void BeginRegion()
    {
        if (Overlay.Active)
            return;

        Overlay.ShowRegion(region =>
            RunCapture(async () =>
            {
                await Task.Delay(OverlaySettleMs);
                return await Snip.RegionAsync(region);
            })
        );
    }

    private static bool LoadWindowShadowSetting()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false);
            return key?.GetValue(WindowShadowValue) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ToggleWindowShadow()
    {
        _includeWindowShadow = !_includeWindowShadow;

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, true);
            key.SetValue(WindowShadowValue, _includeWindowShadow ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void BeginWindowPick()
    {
        if (Overlay.Active)
            return;

        bool includeShadow = _includeWindowShadow;
        Overlay.ShowWindowPick(hwnd =>
        {
            Win32.SetForegroundWindow(hwnd);
            RunCapture(async () =>
            {
                await WaitForActivationAsync(hwnd);
                return await Snip.WindowAsync(hwnd, includeShadow);
            });
        });
    }

    private static async Task WaitForActivationAsync(IntPtr hwnd)
    {
        for (int i = 0; i < ActivationPollLimit && Win32.GetForegroundWindow() != hwnd; i++)
            await Task.Delay(ActivationPollMs);

        await Task.Delay(ActivationSettleMs);
    }

    // Capture is asynchronous and must not block the message loop - the loop is what keeps the
    // overlay and the tray responsive. The result comes back as a posted message so that the
    // balloon is raised on the thread that owns the icon.
    private static void RunCapture(Func<Task<string>> capture)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string path = await capture();
                _lastMessage = Path.GetFileName(path);
                _lastFailed = false;
            }
            catch (Exception e)
            {
                _lastMessage = $"{e.GetType().Name}: {e.Message}";
                _lastFailed = true;
            }

            Win32.PostMessage(_hwnd, WM_CAPTURE_DONE, IntPtr.Zero, IntPtr.Zero);
        });
    }

    // ---- Message loop -----------------------------------------------------------

    private static void RunMessageLoop()
    {
        while (true)
        {
            int result = Win32.GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
            if (result is 0 or -1)
                break;

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_HOTKEY:
                    switch ((int)wParam)
                    {
                        case HotkeyFullScreen:
                            RunCapture(Snip.FullScreenAsync);
                            break;
                        case HotkeyRegion:
                            BeginRegion();
                            break;
                        case HotkeyWindow:
                            BeginWindowPick();
                            break;
                    }
                    return IntPtr.Zero;

                case WM_TRAY_CALLBACK:
                    // Classic (pre-version-4) callback semantics: lParam carries the mouse message.
                    uint mouse = (uint)(long)lParam;
                    if (mouse is WM_RBUTTONUP or WM_LBUTTONUP)
                        ShowMenu();
                    return IntPtr.Zero;

                case WM_COMMAND:
                    Dispatch(Win32.LoWord(wParam));
                    return IntPtr.Zero;

                case WM_CAPTURE_DONE:
                    Notify(_lastFailed ? "Capture failed" : "Screenshot saved", _lastMessage);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch { }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void Report(string message)
    {
        Win32.AttachConsole(0xFFFFFFFF);
        Console.WriteLine(message);
    }

    // Interactive mode is normally reached by double-clicking or from the Run key, where there is
    // no console to print to - a startup failure that only wrote to stdout would look like the app
    // silently doing nothing. Reserved for failures that stop the app starting; a lost hotkey is
    // not one of them, and three message boxes on launch would be worse than the problem.
    private static void Fatal(string message)
    {
        Report(message);
        Win32.MessageBox(IntPtr.Zero, message, "WinSnip", MB_ICONWARNING);
    }
}
