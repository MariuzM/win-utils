using WinView.Native;

namespace WinView;

internal static unsafe class Program
{
    private const uint MB_ICONWARNING = 0x00000030;

    private static int Main(string[] args)
    {
        // Headless decode check exercises GDI+ loading and the EXIF orientation path without
        // needing a desktop to draw on.
        if (args.Length > 1 && args[0] == "--probe")
            return Probe(args[1]);

        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return RunCommand(args[0]);

        IntPtr token = StartGdiPlus();
        if (token == IntPtr.Zero)
        {
            Win32.MessageBox(IntPtr.Zero, "GDI+ failed to start.", "WinView", MB_ICONWARNING);
            return 1;
        }

        try
        {
            if (!ViewerWindow.Create(args.Length > 0 ? args[0] : null))
            {
                Win32.MessageBox(IntPtr.Zero, "Could not create the viewer window.", "WinView", MB_ICONWARNING);
                return 1;
            }

            ViewerWindow.RunMessageLoop();
            return 0;
        }
        finally
        {
            Win32.GdiplusShutdown(token);
        }
    }

    private static int RunCommand(string command)
    {
        try
        {
            switch (command)
            {
                case "--register":
                    string path = ShellRegistration.Register();
                    Win32.MessageBox(
                        IntPtr.Zero,
                        $"WinView is now listed under \"Open with\" for images.\n\n{path}",
                        "WinView", 0);
                    return 0;

                case "--unregister":
                    ShellRegistration.Unregister();
                    Win32.MessageBox(IntPtr.Zero, "WinView has been removed from \"Open with\".", "WinView", 0);
                    return 0;

                default:
                    Win32.MessageBox(
                        IntPtr.Zero,
                        "WinView\n\nUsage:\n  WinView <image>\n  WinView --register\n  WinView --unregister",
                        "WinView", 0);
                    return 1;
            }
        }
        catch (Exception e)
        {
            Win32.MessageBox(IntPtr.Zero, $"{e.GetType().Name}: {e.Message}", "WinView", MB_ICONWARNING);
            return 1;
        }
    }

    private static int Probe(string path)
    {
        Win32.AttachConsole(0xFFFFFFFF);

        IntPtr token = StartGdiPlus();
        if (token == IntPtr.Zero)
        {
            Console.WriteLine("GDI+ failed to start.");
            return 1;
        }

        try
        {
            using ImageDocument? document = ImageDocument.Load(path);
            if (document is null)
            {
                Console.WriteLine($"FAILED to decode: {path}");
                return 1;
            }

            Console.WriteLine($"OK {Path.GetFileName(path)}  {document.Width} x {document.Height}");

            FolderList folder = FolderList.For(path);
            Console.WriteLine($"Folder: {folder.Position}/{folder.Count}");
            return 0;
        }
        finally
        {
            Win32.GdiplusShutdown(token);
        }
    }

    private static IntPtr StartGdiPlus()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        IntPtr token;
        return Win32.GdiplusStartup(&token, &input, IntPtr.Zero) == 0 ? token : IntPtr.Zero;
    }
}
