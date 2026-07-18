using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using WinSnip.Native;
using static WinSnip.Native.Win32Const;

namespace WinSnip.Capture;

// Turns a capture mode into a saved PNG. Every mode ends in the same place: grab an item, take one
// frame, crop, write to the Desktop.
internal static class Snip
{
    public static Task<string> FullScreenAsync()
    {
        Win32.GetCursorPos(out POINT cursor);
        IntPtr monitor = Win32.MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);

        GraphicsCaptureItem item = CaptureEngine.ItemForMonitor(monitor)
            ?? throw new InvalidOperationException("Could not create a capture item for the monitor.");

        return CaptureAndSaveAsync(item, crop: null);
    }

    public static Task<string> WindowAsync(IntPtr hwnd)
    {
        GraphicsCaptureItem item = CaptureEngine.ItemForWindow(hwnd)
            ?? throw new InvalidOperationException("Could not create a capture item for that window.");

        return CaptureAndSaveAsync(item, crop: null);
    }

    // Regions are cut out of a full monitor capture rather than captured directly - WGC has no
    // notion of a sub-rectangle, and cropping the frame is both simpler and pixel-exact.
    public static Task<string> RegionAsync(RECT region)
    {
        var centre = new POINT { X = (region.Left + region.Right) / 2, Y = (region.Top + region.Bottom) / 2 };
        IntPtr monitor = Win32.MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("GetMonitorInfo failed for the selected region.");

        GraphicsCaptureItem item = CaptureEngine.ItemForMonitor(monitor)
            ?? throw new InvalidOperationException("Could not create a capture item for the monitor.");

        // The capture is in monitor-local coordinates; the selection is in virtual-desktop
        // coordinates, so it has to be rebased before it can be used as a crop.
        var local = new RECT(
            region.Left - info.rcMonitor.Left,
            region.Top - info.rcMonitor.Top,
            region.Right - info.rcMonitor.Left,
            region.Bottom - info.rcMonitor.Top);

        return CaptureAndSaveAsync(item, local);
    }

    private static async Task<string> CaptureAndSaveAsync(GraphicsCaptureItem item, RECT? crop)
    {
        Shot shot = await CaptureEngine.CaptureAsync(item);
        try
        {
            BitmapBounds bounds = Clamp(crop, shot.ContentWidth, shot.ContentHeight);
            if (bounds.Width == 0 || bounds.Height == 0)
                throw new InvalidOperationException("The selected area is empty.");

            string path = SaveTarget.NextPath(DateTime.Now);
            await CaptureEngine.SaveAsync(shot, bounds, path);
            return path;
        }
        finally
        {
            shot.Bitmap.Dispose();
        }
    }

    // With no crop this still clamps to the content size, which is what keeps the undefined region
    // of an oversized pool texture out of the saved file.
    private static BitmapBounds Clamp(RECT? crop, int contentWidth, int contentHeight)
    {
        if (crop is not RECT rect)
            return new BitmapBounds
            {
                X = 0,
                Y = 0,
                Width = (uint)Math.Max(0, contentWidth),
                Height = (uint)Math.Max(0, contentHeight),
            };

        int left = Math.Clamp(rect.Left, 0, contentWidth);
        int top = Math.Clamp(rect.Top, 0, contentHeight);
        int right = Math.Clamp(rect.Right, left, contentWidth);
        int bottom = Math.Clamp(rect.Bottom, top, contentHeight);

        return new BitmapBounds
        {
            X = (uint)left,
            Y = (uint)top,
            Width = (uint)(right - left),
            Height = (uint)(bottom - top),
        };
    }
}
