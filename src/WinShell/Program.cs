using WinShell.Native;

namespace WinShell;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
        {
            NativeTaskbar.Restore();
            NativeTaskbar.ResetWorkArea();
            return 0;
        }

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            if (!SettingsWindow.Create())
                return 1;

            SettingsWindow.Standalone = true;
            SettingsWindow.Show();
            RunMessageLoop();
            SettingsWindow.Destroy();
            return 0;
        }

        if (args.Contains("--indicator-status", StringComparer.OrdinalIgnoreCase))
        {
            Win32.CoInitializeEx(IntPtr.Zero, Win32Const.COINIT_APARTMENTTHREADED);
            Report(SystemIndicators.Diagnostics());
            Win32.CoUninitialize();
            return 0;
        }

        if (args.Contains("--toggle-mute", StringComparer.OrdinalIgnoreCase))
        {
            Win32.CoInitializeEx(IntPtr.Zero, Win32Const.COINIT_APARTMENTTHREADED);
            Report(SystemIndicators.ToggleMuteDiagnostic());
            Win32.CoUninitialize();
            return 0;
        }

        if (args.Contains("--search-status", StringComparer.OrdinalIgnoreCase))
        {
            Report(SearchControl.Status());
            return 0;
        }

        if (args.Contains("--disable-search", StringComparer.OrdinalIgnoreCase))
        {
            bool ok = SearchControl.Disable(out string disableMessage);
            Report(disableMessage);
            return ok ? 0 : 1;
        }

        if (args.Contains("--enable-search", StringComparer.OrdinalIgnoreCase))
        {
            bool ok = SearchControl.Enable(out string enableMessage);
            Report(enableMessage);
            return ok ? 0 : 1;
        }

        // Exercises the real launch routing from the command line, which is the only way to
        // tell a broken URI apart from a broken activation path without clicking through the
        // Start menu. COM has to be up first: packaged apps go via the activation manager.
        if (args.Contains("--open", StringComparer.OrdinalIgnoreCase))
        {
            int index = Array.FindIndex(args, a => string.Equals(a, "--open", StringComparison.OrdinalIgnoreCase));

            if (index + 1 >= args.Length)
            {
                Report("--open needs a target, e.g. --open ms-settings:display");
                return 1;
            }

            Win32.CoInitializeEx(IntPtr.Zero, Win32Const.COINIT_APARTMENTTHREADED);
            ShellLaunch.Open(args[index + 1]);
            Win32.CoUninitialize();
            return 0;
        }

        if (args.Contains("--desktop-status", StringComparer.OrdinalIgnoreCase))
        {
            Win32.CoInitializeEx(IntPtr.Zero, Win32Const.COINIT_APARTMENTTHREADED);

            try
            {
                if (!DesktopWindow.Create())
                {
                    Report("Failed to create the desktop window.");
                    return 1;
                }

                int click = Array.FindIndex(args, a => string.Equals(a, "--click", StringComparison.OrdinalIgnoreCase));

                if (click >= 0 && click + 1 < args.Length)
                {
                    string[] parts = args[click + 1].Split(',');

                    if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                        DesktopWindow.SimulateClick(x, y);
                }

                if (args.Contains("--rename", StringComparer.OrdinalIgnoreCase))
                    DesktopWindow.SimulateRename();

                if (args.Contains("--menus", StringComparer.OrdinalIgnoreCase))
                    Report(DesktopWindow.ProbeContextMenus());

                Report(DesktopWindow.Diagnostics());
                return 0;
            }
            finally
            {
                DesktopWindow.Destroy();
                Win32.CoUninitialize();
            }
        }

        // Runs the desktop on its own, with no taskbar and no tray. Two uses: checking the
        // wallpaper and icons render correctly without restarting the shell, and getting a
        // desktop back on a machine already running an older WinShell as its shell.
        if (args.Contains("--desktop-only", StringComparer.OrdinalIgnoreCase))
        {
            Win32.CoInitializeEx(IntPtr.Zero, Win32Const.COINIT_APARTMENTTHREADED);

            try
            {
                if (!DesktopWindow.Create())
                {
                    Report("Failed to create the desktop window.");
                    return 1;
                }

                RunMessageLoop();
                return 0;
            }
            finally
            {
                DesktopWindow.Destroy();
                Win32.CoUninitialize();
            }
        }

        if (args.Contains("--shell-status", StringComparer.OrdinalIgnoreCase))
        {
            Report(ShellRegistration.Status());
            return 0;
        }

        if (args.Contains("--install-shell", StringComparer.OrdinalIgnoreCase))
        {
            bool ok = ShellRegistration.Install(out string installMessage);
            Report(installMessage);
            return ok ? 0 : 1;
        }

        if (args.Contains("--uninstall-shell", StringComparer.OrdinalIgnoreCase))
        {
            bool ok = ShellRegistration.Uninstall(out string uninstallMessage);
            Report(uninstallMessage);
            return ok ? 0 : 1;
        }

        // --no-hide leaves the built-in taskbar visible. Essential while developing: it means
        // a crash or a half-finished feature never leaves the machine with no taskbar at all.
        bool hideNative = !args.Contains("--no-hide", StringComparer.OrdinalIgnoreCase);

        // Restoring the native taskbar is registered before anything can fail, so every exit
        // path - clean shutdown, unhandled exception, or Ctrl+C - puts the shell back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => NativeTaskbar.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => NativeTaskbar.Restore();
        Console.CancelKeyPress += (_, _) => NativeTaskbar.Restore();

        Win32.CoInitializeEx(IntPtr.Zero, Win32Const.COINIT_APARTMENTTHREADED);

        // Progman is Explorer's desktop window. Its presence is the reliable test for "someone
        // is already drawing a desktop" - checking whether explorer.exe is *running* would be
        // wrong, because an open File Explorer window is also an explorer.exe.
        bool explorerOwnsDesktop = Win32.FindWindow("Progman", null) != IntPtr.Zero;

        try
        {
            if (!TaskbarWindow.Create())
            {
                NativeTaskbar.Restore();
                return 1;
            }

            // Only when we are actually the shell. Alongside a live Explorer the real desktop
            // already exists, and painting a second one over it would cover the first.
            if (!explorerOwnsDesktop || args.Contains("--desktop", StringComparer.OrdinalIgnoreCase))
                DesktopWindow.Create();

            StartMenuWindow.Create();
            VolumeFlyout.Create();
            SettingsWindow.Create();

            if (hideNative)
            {
                NativeTaskbar.Hide();
                TaskbarWindow.Redock();
            }

            bool explorerOwnsTray = Win32.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

            if (!explorerOwnsTray || args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
                TrayHost.Create();

            RunMessageLoop();
            return 0;
        }
        finally
        {
            TrayHost.Destroy();
            SettingsWindow.Destroy();
            VolumeFlyout.Destroy();
            StartMenuWindow.Destroy();
            DesktopWindow.Destroy();
            TaskbarWindow.Destroy();
            NativeTaskbar.Restore();
            Win32.CoUninitialize();
        }
    }

    private static void Report(string text)
    {
        const uint AttachParentProcess = 0xFFFFFFFF;

        if (Win32.AttachConsole(AttachParentProcess))
        {
            var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(writer);
        }

        Console.WriteLine(text);
    }

    private static void RunMessageLoop()
    {
        // GetMessage blocks until something arrives, so an idle taskbar consumes no CPU at
        // all - the window list is driven by WinEvent hooks and the only timer is the clock.
        while (true)
        {
            int result = Win32.GetMessage(out MSG msg, IntPtr.Zero, 0, 0);

            // 0 is WM_QUIT; -1 is an error, and continuing would spin forever.
            if (result is 0 or -1)
                break;

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }
}
