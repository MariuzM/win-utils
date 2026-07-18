using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;
using WinSnip.Native;

namespace WinSnip.Capture;

// A single captured frame already copied to the CPU, plus the sub-rectangle of it that actually
// contains content. WGC frames come from a pooled texture that can be larger than the source, and
// Microsoft documents the remainder as undefined data - so the content size always has to travel
// with the bitmap rather than being inferred from its dimensions.
internal readonly record struct Shot(SoftwareBitmap Bitmap, int ContentWidth, int ContentHeight);

// Not marked unsafe at class level: that would make every member an unsafe context, and await is
// not allowed inside one. Only the handful of methods that dispatch vtable slots are unsafe.
internal static class CaptureEngine
{
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;

    // From d3d11.h. Not published on Learn, and there is no constant for it in the projections.
    private const uint D3D11_SDK_VERSION = 7;

    // Learn documents BGRA_SUPPORT as a Direct2D interop requirement, but every Microsoft WGC
    // sample passes it and the capture format is BGRA, so treat it as required here too.
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    private const int CaptureTimeoutMs = 3000;

    private const string CaptureSessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";

    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static IDirect3DDevice? _device;

    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    // ---- COM plumbing -----------------------------------------------------------
    //
    // Only IUnknown slots are dispatched by hand (QueryInterface 0, Release 2). Those three slots
    // are fixed by the COM ABI itself, so unlike a D3D11 method index they cannot be wrong.

    private static unsafe int QueryInterface(IntPtr unknown, Guid iid, out IntPtr result)
    {
        void** vtbl = *(void***)unknown;
        var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        IntPtr local;
        int hr = queryInterface(unknown, &iid, &local);
        result = local;
        return hr;
    }

    private static unsafe void Release(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero)
            return;

        void** vtbl = *(void***)unknown;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        release(unknown);
    }

    // ---- Device -----------------------------------------------------------------

    private static IDirect3DDevice Device()
    {
        if (_device is not null)
            return _device;

        IntPtr d3dDevice = CreateD3DDevice();
        try
        {
            int hr = QueryInterface(d3dDevice, IID_IDXGIDevice, out IntPtr dxgiDevice);
            if (hr < 0)
                throw new InvalidOperationException($"QueryInterface(IDXGIDevice) failed: 0x{hr:X8}");

            try
            {
                hr = Win32.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
                if (hr < 0)
                    throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");

                _device = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
                return _device;
            }
            finally
            {
                Release(dxgiDevice);
            }
        }
        finally
        {
            Release(d3dDevice);
        }
    }

    private static IntPtr CreateD3DDevice()
    {
        // Null feature-level list means "whatever the driver offers", which is all this needs -
        // the device is only ever a handle passed to the frame pool.
        int hr = Win32.D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out IntPtr device, IntPtr.Zero, IntPtr.Zero);

        // A VM with no usable 3D driver still has to work, so fall back to the software rasteriser.
        if (hr < 0)
        {
            hr = Win32.D3D11CreateDevice(
                IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, IntPtr.Zero, IntPtr.Zero);
        }

        if (hr < 0)
            throw new InvalidOperationException($"D3D11CreateDevice failed for both hardware and WARP: 0x{hr:X8}");

        return device;
    }

    // ---- Capture items ----------------------------------------------------------

    public static GraphicsCaptureItem? ItemForWindow(IntPtr hwnd) => CreateItem(hwnd, slot: 3);

    public static GraphicsCaptureItem? ItemForMonitor(IntPtr monitor) => CreateItem(monitor, slot: 4);

    // IGraphicsCaptureItemInterop derives from IUnknown, not IInspectable, so CreateForWindow is
    // vtable slot 3 and CreateForMonitor is slot 4. Assuming an IInspectable base (slots 6 and 7)
    // is the classic way to crash here.
    private static unsafe GraphicsCaptureItem? CreateItem(IntPtr handle, int slot)
    {
        if (handle == IntPtr.Zero)
            return null;

        IntPtr factory = ActivationFactory();
        try
        {
            void** vtbl = *(void***)factory;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot];

            Guid iid = IID_GraphicsCaptureItem;
            IntPtr abi;
            int hr = create(factory, handle, &iid, &abi);

            // A window that closed between being picked and being captured returns E_INVALIDARG,
            // which is a normal race rather than a fault - report it as "no item".
            if (hr < 0 || abi == IntPtr.Zero)
                return null;

            // FromAbi produces a projected wrapper over the ABI pointer. Whether it takes ownership
            // of the reference or adds its own is not documented, so the raw pointer is
            // deliberately not released: leaking one COM reference per screenshot is harmless,
            // while releasing one we do not own would be a use-after-free.
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);
        }
        finally
        {
            Release(factory);
        }
    }

    private static unsafe IntPtr ActivationFactory()
    {
        const string typeName = "Windows.Graphics.Capture.GraphicsCaptureItem";

        fixed (char* name = typeName)
        {
            int hr = Win32.WindowsCreateString(name, (uint)typeName.Length, out IntPtr hstring);
            if (hr < 0)
                throw new InvalidOperationException($"WindowsCreateString failed: 0x{hr:X8}");

            try
            {
                hr = Win32.RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out IntPtr factory);
                if (hr < 0)
                    throw new InvalidOperationException($"RoGetActivationFactory failed: 0x{hr:X8}");

                return factory;
            }
            finally
            {
                Win32.WindowsDeleteString(hstring);
            }
        }
    }

    // ---- One-shot capture -------------------------------------------------------

    public static async Task<Shot> CaptureAsync(GraphicsCaptureItem item)
    {
        IDirect3DDevice device = Device();

        // CreateFreeThreaded, never Create: Create needs a DispatcherQueue on the calling thread,
        // and without one it does not fail cleanly - it hangs. One buffer is enough for a
        // single-shot grab.
        Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, numberOfBuffers: 1, item.Size);

        GraphicsCaptureSession session = pool.CreateCaptureSession(item);
        ApplySessionOptions(session);

        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            // TryGetNextFrame can legitimately return null; only the first non-null frame matters.
            Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is not null)
                arrived.TrySetResult(frame);
        };

        Direct3D11CaptureFrame? captured = null;
        try
        {
            session.StartCapture();

            // Bounded wait rather than a fixed "skip N frames" warm-up: Microsoft's own one-shot
            // sample takes frame 1 directly, and the first-frame-is-blank folklore is not
            // documented anywhere. A timeout still guards the case where no frame ever arrives.
            Task<Direct3D11CaptureFrame> frameTask = arrived.Task;
            if (await Task.WhenAny(frameTask, Task.Delay(CaptureTimeoutMs)) != frameTask)
                throw new TimeoutException($"No capture frame arrived within {CaptureTimeoutMs} ms.");

            captured = await frameTask;

            SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                captured.Surface, BitmapAlphaMode.Premultiplied);

            return new Shot(bitmap, captured.ContentSize.Width, captured.ContentSize.Height);
        }
        finally
        {
            session.Dispose();
            pool.Dispose();
            captured?.Dispose();
        }
    }

    // Both properties are best-effort. On older builds CsWinRT surfaces the missing interface as a
    // cast exception rather than returning gracefully, so the presence check and the try/catch are
    // both needed - and borderless is documented as capability-gated, meaning the assignment can
    // silently do nothing.
    private static void ApplySessionOptions(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent(CaptureSessionType, "IsCursorCaptureEnabled"))
                session.IsCursorCaptureEnabled = false;
        }
        catch
        {
        }

        try
        {
            if (ApiInformation.IsPropertyPresent(CaptureSessionType, "IsBorderRequired"))
                session.IsBorderRequired = false;
        }
        catch
        {
        }
    }

    // ---- Encoding ---------------------------------------------------------------

    // Cropping is done by the encoder rather than on the bitmap: it is the same call that trims the
    // frame down to its content size, so region capture costs nothing extra.
    public static async Task SaveAsync(Shot shot, BitmapBounds bounds, string path)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"Path has no directory: {path}", nameof(path));

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
        StorageFile file = await folder.CreateFileAsync(
            Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);

        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(shot.Bitmap);
        encoder.BitmapTransform.Bounds = bounds;
        await encoder.FlushAsync();
    }
}
