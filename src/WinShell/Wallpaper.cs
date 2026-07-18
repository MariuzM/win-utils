using Microsoft.Win32;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>How the image is mapped onto the screen. Matches DESKTOP_WALLPAPER_POSITION.</summary>
internal enum WallpaperFit
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

/// <summary>
/// Decodes the current wallpaper once and blits it on demand.
///
/// Explorer normally does this, so with WinShell as the shell nobody does and the screen is
/// left showing the bare desktop background colour - the "dark background" symptom. There is
/// no API that says "paint the wallpaper here"; the image has to be found, decoded and
/// composited by hand.
///
/// GDI cannot decode anything but BMP and real wallpapers are JPEG or PNG, so this is the one
/// place GDI+ is used. It is the flat C API rather than System.Drawing, which keeps the AOT
/// analyzers quiet, and the decode happens exactly once - what is kept afterwards is a plain
/// HBITMAP that costs nothing to blit.
/// </summary>
internal static unsafe class Wallpaper
{
    private static readonly Guid ClsidDesktopWallpaper = new("c2cf3110-460e-4fc1-b9d0-8a1c0c9cc4bd");
    private static readonly Guid IidDesktopWallpaper = new("b92b56a9-8b55-4e14-9a89-0199bbb6f93b");

    private static IntPtr _gdiplusToken;
    private static IntPtr _bitmap;
    private static int _sourceWidth;
    private static int _sourceHeight;

    private static WallpaperFit _fit = WallpaperFit.Fill;
    private static uint _background = Win32.Rgb(0, 0, 0);

    public static void Initialize()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };

        IntPtr token;
        if (Win32.GdiplusStartup(&token, &input, IntPtr.Zero) == 0)
            _gdiplusToken = token;

        Reload();
    }

    public static void Shutdown()
    {
        ReleaseBitmap();

        if (_gdiplusToken != IntPtr.Zero)
        {
            Win32.GdiplusShutdown(_gdiplusToken);
            _gdiplusToken = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Re-reads the wallpaper. Cheap to call on WM_SETTINGCHANGE, which is how a wallpaper
    /// change made in Settings reaches us - there is no notification aimed at the shell.
    /// </summary>
    public static void Reload()
    {
        ReleaseBitmap();

        string path = ReadSettings();

        if (path.Length > 0 && File.Exists(path))
            Load(path);
    }

    private static void ReleaseBitmap()
    {
        if (_bitmap != IntPtr.Zero)
        {
            Win32.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }

        _sourceWidth = 0;
        _sourceHeight = 0;
    }

    // ---- Reading the current settings -------------------------------------------------

    /// <summary>
    /// Asks the shell first and falls back to the registry. IDesktopWallpaper is the accurate
    /// source - it reports the real position enum and resolves per-monitor images - but it is
    /// a shell COM server, so on a machine where the shell plumbing is unhappy it can fail.
    /// The registry values it mirrors are always readable.
    /// </summary>
    private static string ReadSettings()
    {
        string path = QueryShell();

        if (path.Length > 0)
            return path;

        return QueryRegistry();
    }

    private static string QueryShell()
    {
        IntPtr wallpaper = IntPtr.Zero;

        try
        {
            Guid clsid = ClsidDesktopWallpaper;
            Guid iid = IidDesktopWallpaper;

            IntPtr ppv;
            if (Win32.CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iid, &ppv) != 0 || ppv == IntPtr.Zero)
                return string.Empty;

            wallpaper = ppv;

            uint position;
            if (GetPosition(wallpaper, &position) == 0)
                _fit = (WallpaperFit)position;

            uint color;
            if (GetBackgroundColor(wallpaper, &color) == 0)
                _background = color;

            // A null monitor ID means "whatever is on the primary monitor". Per-monitor
            // wallpapers are not handled yet; neither is the taskbar, so they stay in step.
            IntPtr buffer;
            if (GetWallpaper(wallpaper, null, &buffer) != 0 || buffer == IntPtr.Zero)
                return string.Empty;

            try
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Win32.CoTaskMemFree(buffer);
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (wallpaper != IntPtr.Zero)
                Release(wallpaper);
        }
    }

    private static string QueryRegistry()
    {
        try
        {
            using RegistryKey? desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");

            if (desktop == null)
                return string.Empty;

            // WallpaperStyle and TileWallpaper predate the position enum and encode the same
            // thing between them: style 10 is Fill, 6 is Fit, 2 is Stretch, 0 is Center
            // unless TileWallpaper is set, in which case 0 means Tile.
            string style = desktop.GetValue("WallpaperStyle") as string ?? "10";
            string tile = desktop.GetValue("TileWallpaper") as string ?? "0";

            _fit = style switch
            {
                "22" => WallpaperFit.Span,
                "10" => WallpaperFit.Fill,
                "6" => WallpaperFit.Fit,
                "2" => WallpaperFit.Stretch,
                _ => tile == "1" ? WallpaperFit.Tile : WallpaperFit.Center,
            };

            _background = ReadBackgroundColor();

            return desktop.GetValue("WallPaper") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Reads Control Panel\Colors\Background, stored as "R G B" decimal text.</summary>
    private static uint ReadBackgroundColor()
    {
        try
        {
            using RegistryKey? colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");

            if (colors?.GetValue("Background") is not string value)
                return _background;

            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 3 ||
                !byte.TryParse(parts[0], out byte r) ||
                !byte.TryParse(parts[1], out byte g) ||
                !byte.TryParse(parts[2], out byte b))
            {
                return _background;
            }

            return Win32.Rgb(r, g, b);
        }
        catch
        {
            return _background;
        }
    }

    private static void Load(string path)
    {
        IntPtr image;

        fixed (char* name = path)
        {
            if (Win32.GdipCreateBitmapFromFile(name, &image) != 0 || image == IntPtr.Zero)
                return;
        }

        try
        {
            uint width;
            uint height;

            if (Win32.GdipGetImageWidth(image, &width) != 0 || Win32.GdipGetImageHeight(image, &height) != 0)
                return;

            // Opaque black behind any alpha channel: a wallpaper is never composited against
            // anything, and leaving it premultiplied would show as fringing.
            IntPtr hbitmap;
            if (Win32.GdipCreateHBITMAPFromBitmap(image, &hbitmap, 0xFF000000) != 0 || hbitmap == IntPtr.Zero)
                return;

            _bitmap = hbitmap;
            _sourceWidth = (int)width;
            _sourceHeight = (int)height;
        }
        finally
        {
            Win32.GdipDisposeImage(image);
        }
    }

    // ---- Painting ------------------------------------------------------------------------

    /// <summary>
    /// Fills the given device context with the wallpaper. Always paints something: with no
    /// image (or a failed decode) it falls back to the desktop background colour, which is
    /// what the user would have seen under Explorer anyway.
    /// </summary>
    public static void Paint(IntPtr hdc, int width, int height)
    {
        if (_bitmap == IntPtr.Zero)
        {
            FillBackground(hdc, width, height);
            return;
        }

        IntPtr source = Win32.CreateCompatibleDC(hdc);

        if (source == IntPtr.Zero)
        {
            FillBackground(hdc, width, height);
            return;
        }

        IntPtr previous = Win32.SelectObject(source, _bitmap);

        // HALFTONE resamples instead of dropping scanlines. It costs more per blit, but a 4K
        // wallpaper scaled to a smaller screen looks visibly broken without it. The brush
        // origin has to be reset afterwards, which is a documented HALFTONE requirement.
        Win32.SetStretchBltMode(hdc, HALFTONE);
        Win32.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);

        switch (_fit)
        {
            case WallpaperFit.Tile:
                PaintTiled(hdc, source, width, height);
                break;

            case WallpaperFit.Center:
                PaintCentered(hdc, source, width, height);
                break;

            case WallpaperFit.Stretch:
                Win32.StretchBlt(hdc, 0, 0, width, height, source, 0, 0, _sourceWidth, _sourceHeight, SRCCOPY);
                break;

            case WallpaperFit.Fit:
                PaintContained(hdc, source, width, height);
                break;

            // Span behaves as Fill until the desktop is multi-monitor aware.
            default:
                PaintCovered(hdc, source, width, height);
                break;
        }

        Win32.SelectObject(source, previous);
        Win32.DeleteDC(source);
    }

    private static void FillBackground(IntPtr hdc, int width, int height)
    {
        IntPtr brush = Win32.CreateSolidBrush(_background);
        var rect = new RECT(0, 0, width, height);

        Win32.FillRect(hdc, ref rect, brush);
        Win32.DeleteObject(brush);
    }

    /// <summary>Fill: scale to cover the screen, cropping the overhanging axis evenly.</summary>
    private static void PaintCovered(IntPtr hdc, IntPtr source, int width, int height)
    {
        int cropWidth = _sourceWidth;
        int cropHeight = _sourceHeight;

        // Compare aspect ratios by cross-multiplying, which avoids the float division and
        // the rounding that comes with it.
        if ((long)_sourceWidth * height > (long)width * _sourceHeight)
            cropWidth = (int)((long)_sourceHeight * width / height);
        else
            cropHeight = (int)((long)_sourceWidth * height / width);

        int x = (_sourceWidth - cropWidth) / 2;
        int y = (_sourceHeight - cropHeight) / 2;

        Win32.StretchBlt(hdc, 0, 0, width, height, source, x, y, cropWidth, cropHeight, SRCCOPY);
    }

    /// <summary>Fit: scale to sit entirely inside the screen, letterboxed on the short axis.</summary>
    private static void PaintContained(IntPtr hdc, IntPtr source, int width, int height)
    {
        FillBackground(hdc, width, height);

        int drawWidth = width;
        int drawHeight = height;

        if ((long)_sourceWidth * height > (long)width * _sourceHeight)
            drawHeight = (int)((long)_sourceHeight * width / _sourceWidth);
        else
            drawWidth = (int)((long)_sourceWidth * height / _sourceHeight);

        Win32.StretchBlt(
            hdc, (width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight,
            source, 0, 0, _sourceWidth, _sourceHeight, SRCCOPY);
    }

    private static void PaintCentered(IntPtr hdc, IntPtr source, int width, int height)
    {
        FillBackground(hdc, width, height);

        // Offsets go negative when the image is larger than the screen, which crops it
        // symmetrically - exactly what Center is supposed to do.
        int x = (width - _sourceWidth) / 2;
        int y = (height - _sourceHeight) / 2;

        Win32.BitBlt(hdc, x, y, _sourceWidth, _sourceHeight, source, 0, 0, SRCCOPY);
    }

    private static void PaintTiled(IntPtr hdc, IntPtr source, int width, int height)
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0)
            return;

        for (int y = 0; y < height; y += _sourceHeight)
        {
            for (int x = 0; x < width; x += _sourceWidth)
            {
                Win32.BitBlt(hdc, x, y, _sourceWidth, _sourceHeight, source, 0, 0, SRCCOPY);
            }
        }
    }

    // ---- IDesktopWallpaper, by vtable slot ------------------------------------------------

    private static uint Release(IntPtr obj)
    {
        var fn = (delegate* unmanaged<IntPtr, uint>)(*(void***)obj)[2];
        return fn(obj);
    }

    private static int GetWallpaper(IntPtr self, char* monitorId, IntPtr* wallpaper)
    {
        var fn = (delegate* unmanaged<IntPtr, char*, IntPtr*, int>)(*(void***)self)[4];
        return fn(self, monitorId, wallpaper);
    }

    private static int GetBackgroundColor(IntPtr self, uint* color)
    {
        var fn = (delegate* unmanaged<IntPtr, uint*, int>)(*(void***)self)[9];
        return fn(self, color);
    }

    private static int GetPosition(IntPtr self, uint* position)
    {
        var fn = (delegate* unmanaged<IntPtr, uint*, int>)(*(void***)self)[11];
        return fn(self, position);
    }
}
