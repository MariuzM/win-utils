using WinSnip.Capture;
using WinSnip.Native;

namespace WinSnip;

internal static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static int Main(string[] args)
    {
        // Headless subcommands exercise the capture path without the tray, hotkeys or overlay so
        // capture bugs can be isolated from UI bugs.
        if (args.Length > 0)
            return RunCommand(args);

        return Ui.App.Run();
    }

    private static int RunCommand(string[] args)
    {
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--check":
                    Report($"GraphicsCaptureSession.IsSupported: {CaptureEngine.IsSupported()}");
                    Report($"Desktop folder: {SaveTarget.DesktopFolder()}");
                    return 0;

                case "--full":
                    return Save(Snip.FullScreenAsync());

                case "--window":
                    return Save(Snip.WindowAsync(ResolveWindow(args), HasArgument(args, "--shadow")));

                case "--region":
                    return Save(Snip.RegionAsync(ParseRegion(args)));

                default:
                    Report($"Unknown option: {args[0]}");
                    return 1;
            }
        }
        catch (Exception e)
        {
            Report($"Failed: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Save(Task<string> capture)
    {
        string path = capture.GetAwaiter().GetResult();
        Report(path);
        return 0;
    }

    private static IntPtr ResolveWindow(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("--window needs a window handle, or 'foreground'.");

        if (args[1].Equals("foreground", StringComparison.OrdinalIgnoreCase))
        {
            Win32.GetCursorPos(out POINT cursor);
            IntPtr hwnd = Win32.GetAncestor(Win32.WindowFromPoint(cursor), Win32Const.GA_ROOT);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("No window under the cursor.");

            return hwnd;
        }

        return new IntPtr(long.Parse(args[1]));
    }

    private static bool HasArgument(string[] args, string expected)
    {
        foreach (string value in args)
        {
            if (value.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static RECT ParseRegion(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("--region needs x,y,width,height.");

        string[] parts = args[1].Split(',');
        if (parts.Length != 4)
            throw new ArgumentException("--region takes exactly four comma-separated numbers.");

        int x = int.Parse(parts[0]);
        int y = int.Parse(parts[1]);
        int width = int.Parse(parts[2]);
        int height = int.Parse(parts[3]);

        return new RECT(x, y, x + width, y + height);
    }

    // The app is a WinExe, so it has no console of its own; borrowing the parent's is what makes
    // the diagnostic subcommands readable from PowerShell.
    private static void Report(string message)
    {
        Win32.AttachConsole(AttachParentProcess);
        Console.WriteLine(message);
    }
}
