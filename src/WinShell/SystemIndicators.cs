using WinShell.Native;

namespace WinShell;

internal enum IndicatorKind
{
    Volume,
    Network,
    Battery,
}

internal sealed class Indicator
{
    public IndicatorKind Kind;
    public string Glyph = string.Empty;
    public string Tip = string.Empty;
    public bool Visible = true;
    public RECT Bounds;
}

internal static unsafe class SystemIndicators
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const uint ClsctxAll = 23;
    private const int RenderDataFlow = 0;
    private const int MultimediaRole = 1;

    private static readonly Indicator VolumeIndicator = new() { Kind = IndicatorKind.Volume };
    private static readonly Indicator NetworkIndicator = new() { Kind = IndicatorKind.Network };
    private static readonly Indicator BatteryIndicator = new() { Kind = IndicatorKind.Battery };

    private static readonly Indicator[] AllIndicators = [VolumeIndicator, NetworkIndicator, BatteryIndicator];

    private static IntPtr _endpointVolume;

    public static IReadOnlyList<Indicator> Items => AllIndicators;

    public static void Initialize()
    {
        _endpointVolume = OpenEndpointVolume();
        Refresh();
    }

    public static void Shutdown()
    {
        if (_endpointVolume != IntPtr.Zero)
        {
            Release(_endpointVolume);
            _endpointVolume = IntPtr.Zero;
        }
    }

    public static bool Refresh()
    {
        string volumeGlyph = VolumeIndicator.Glyph;
        string networkGlyph = NetworkIndicator.Glyph;
        string batteryGlyph = BatteryIndicator.Glyph;
        bool batteryVisible = BatteryIndicator.Visible;

        RefreshVolume();
        RefreshNetwork();
        RefreshBattery();

        return volumeGlyph != VolumeIndicator.Glyph
            || networkGlyph != NetworkIndicator.Glyph
            || batteryGlyph != BatteryIndicator.Glyph
            || batteryVisible != BatteryIndicator.Visible;
    }

    private static void RefreshVolume()
    {
        if (!TryGetVolume(out float level, out bool muted))
        {
            VolumeIndicator.Glyph = "";
            VolumeIndicator.Tip = "Volume unavailable";
            return;
        }

        int percent = (int)MathF.Round(level * 100f);

        VolumeIndicator.Glyph = muted || percent == 0 ? ""
            : percent < 34 ? ""
            : percent < 67 ? ""
            : "";

        VolumeIndicator.Tip = muted ? "Muted" : $"Volume {percent}%";
    }

    private static void RefreshNetwork()
    {
        bool connected = Win32.InternetGetConnectedState(out uint flags, 0);

        const uint ConnectionModem = 0x01;

        NetworkIndicator.Glyph = connected ? "" : "";
        NetworkIndicator.Tip = connected
            ? (flags & ConnectionModem) != 0 ? "Connected (modem)" : "Connected"
            : "No network connection";
    }

    private static void RefreshBattery()
    {
        if (!Win32.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
        {
            BatteryIndicator.Visible = false;
            return;
        }

        const byte NoSystemBattery = 128;
        const byte UnknownPercent = 255;

        if ((status.BatteryFlag & NoSystemBattery) != 0)
        {
            BatteryIndicator.Visible = false;
            return;
        }

        BatteryIndicator.Visible = true;

        bool charging = status.ACLineStatus == 1;
        int percent = status.BatteryLifePercent == UnknownPercent ? -1 : status.BatteryLifePercent;

        if (percent < 0)
        {
            BatteryIndicator.Glyph = "";
            BatteryIndicator.Tip = "Battery status unknown";
            return;
        }

        // Segoe MDL2 battery glyphs run E850..E85A in 10% steps, with the charging set at E85B.
        int step = Math.Clamp(percent / 10, 0, 10);
        BatteryIndicator.Glyph = charging
            ? ((char)('' + step)).ToString()
            : ((char)('' + step)).ToString();

        BatteryIndicator.Tip = charging ? $"Charging {percent}%" : $"Battery {percent}%";
    }

    public static void AdjustVolume(int notches)
    {
        if (_endpointVolume == IntPtr.Zero || !TryGetVolume(out float level, out _))
            return;

        float target = Math.Clamp(level + (notches * 0.02f), 0f, 1f);
        SetMasterVolumeScalar(_endpointVolume, target);

        if (target > 0f)
            SetMute(_endpointVolume, false);
    }

    public static void SetVolume(float level)
    {
        if (_endpointVolume == IntPtr.Zero)
            return;

        float target = Math.Clamp(level, 0f, 1f);
        SetMasterVolumeScalar(_endpointVolume, target);
        SetMute(_endpointVolume, target <= 0f);
    }

    public static void CurrentVolume(out float level, out bool muted)
    {
        if (!TryGetVolume(out level, out muted))
        {
            level = 0f;
            muted = false;
        }
    }

    public static void ToggleMute()
    {
        if (_endpointVolume == IntPtr.Zero || !TryGetVolume(out _, out bool muted))
            return;

        SetMute(_endpointVolume, !muted);
    }

    public static string Diagnostics()
    {
        Initialize();

        string volume = TryGetVolume(out float level, out bool muted)
            ? $"level={(int)MathF.Round(level * 100f)}% muted={muted} glyph={VolumeIndicator.Glyph}"
            : "UNAVAILABLE (Core Audio endpoint not opened)";

        string result =
            $"volume : {volume}{Environment.NewLine}" +
            $"network: {NetworkIndicator.Tip} glyph={NetworkIndicator.Glyph}{Environment.NewLine}" +
            $"battery: visible={BatteryIndicator.Visible} {BatteryIndicator.Tip} glyph={BatteryIndicator.Glyph}";

        Shutdown();
        return result;
    }

    public static string ToggleMuteDiagnostic()
    {
        Initialize();

        if (!TryGetVolume(out _, out bool before))
        {
            Shutdown();
            return "volume endpoint unavailable";
        }

        ToggleMute();
        TryGetVolume(out _, out bool after);
        Shutdown();

        return $"mute {before} -> {after}  (changed={before != after})";
    }

    public static void Open(IndicatorKind kind)
    {
        string target = kind switch
        {
            IndicatorKind.Volume => "ms-settings:sound",
            IndicatorKind.Network => "ms-settings:network",
            _ => "ms-settings:powersleep",
        };

        ShellLaunch.Open(target);
    }

    private static bool TryGetVolume(out float level, out bool muted)
    {
        level = 0f;
        muted = false;

        if (_endpointVolume == IntPtr.Zero)
            return false;

        float value;
        if (GetMasterVolumeScalar(_endpointVolume, &value) != 0)
            return false;

        int mute;
        if (GetMute(_endpointVolume, &mute) != 0)
            return false;

        level = value;
        muted = mute != 0;
        return true;
    }

    private static IntPtr OpenEndpointVolume()
    {
        IntPtr enumerator = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;

        try
        {
            Guid clsid = ClsidMMDeviceEnumerator;
            Guid iidEnum = IidMMDeviceEnumerator;

            IntPtr pEnum;
            if (Win32.CoCreateInstance(&clsid, IntPtr.Zero, ClsctxAll, &iidEnum, &pEnum) != 0 || pEnum == IntPtr.Zero)
                return IntPtr.Zero;

            enumerator = pEnum;

            IntPtr pDevice;
            if (GetDefaultAudioEndpoint(enumerator, RenderDataFlow, MultimediaRole, &pDevice) != 0 || pDevice == IntPtr.Zero)
                return IntPtr.Zero;

            device = pDevice;

            Guid iidVolume = IidAudioEndpointVolume;
            IntPtr pVolume;
            if (Activate(device, &iidVolume, ClsctxAll, IntPtr.Zero, &pVolume) != 0)
                return IntPtr.Zero;

            return pVolume;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (device != IntPtr.Zero)
                Release(device);

            if (enumerator != IntPtr.Zero)
                Release(enumerator);
        }
    }

    private static uint Release(IntPtr obj)
    {
        var fn = (delegate* unmanaged<IntPtr, uint>)(*(void***)obj)[2];
        return fn(obj);
    }

    private static int GetDefaultAudioEndpoint(IntPtr self, int flow, int role, IntPtr* device)
    {
        var fn = (delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)(*(void***)self)[4];
        return fn(self, flow, role, device);
    }

    private static int Activate(IntPtr self, Guid* iid, uint clsctx, IntPtr activationParams, IntPtr* result)
    {
        var fn = (delegate* unmanaged<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)(*(void***)self)[3];
        return fn(self, iid, clsctx, activationParams, result);
    }

    private static int SetMasterVolumeScalar(IntPtr self, float level)
    {
        var fn = (delegate* unmanaged<IntPtr, float, Guid*, int>)(*(void***)self)[7];
        return fn(self, level, null);
    }

    private static int GetMasterVolumeScalar(IntPtr self, float* level)
    {
        var fn = (delegate* unmanaged<IntPtr, float*, int>)(*(void***)self)[9];
        return fn(self, level);
    }

    private static int SetMute(IntPtr self, bool mute)
    {
        var fn = (delegate* unmanaged<IntPtr, int, Guid*, int>)(*(void***)self)[14];
        return fn(self, mute ? 1 : 0, null);
    }

    private static int GetMute(IntPtr self, int* mute)
    {
        var fn = (delegate* unmanaged<IntPtr, int*, int>)(*(void***)self)[15];
        return fn(self, mute);
    }
}
