using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// One place that knows how to start things, because "start this" stopped being a single call
/// once WinShell became the shell.
///
/// ShellExecute is no longer sufficient on its own. With Explorer not registered as the shell,
/// the immersive-shell activation path that ShellExecute delegates to for packaged apps is not
/// registered either, so <c>shell:AppsFolder\...</c> fails with "class not registered" and
/// <c>ms-settings:</c> fails with 0x80040900. Neither is a bad URI - it is a side effect of
/// replacing the shell, and it silently broke every Store app in the Start menu along with the
/// entire Settings catalogue.
///
/// Targets are therefore routed by what they actually are:
///   packaged app (AUMID) -> IApplicationActivationManager, which does not involve Explorer
///   everything else      -> ShellExecute, falling back to explorer.exe when it refuses
/// </summary>
internal static unsafe class ShellLaunch
{
    private static readonly Guid ClsidApplicationActivationManager = new("45ba127d-10a8-46ea-8ab7-56ea9078943c");
    private static readonly Guid IidApplicationActivationManager = new("2e941141-7f97-4756-ba1d-9decde894a3d");

    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    // ShellExperienceHost is the immersive shell. Explorer starts it as part of being the
    // shell; nobody starts it when WinShell is, which is why UWP windows never appear.
    private const string ImmersiveShellAppId = "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy!App";

    private static bool _immersiveShellChecked;

    // ShellExecute returns a fake HINSTANCE: anything at or below 32 is an error code rather
    // than a handle. This is the documented way to tell the two apart.
    private const int ShellExecuteMaxError = 32;

    /// <summary>Opens a path, a URI, or an AppsFolder entry - whichever it turns out to be.</summary>
    public static void Open(string target)
    {
        if (target.Length == 0)
            return;

        if (target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string id = target[AppsFolderPrefix.Length..];

            if (IsPackagedAppId(id))
            {
                EnsureImmersiveShell();

                if (ActivatePackaged(id))
                    return;
            }
        }

        // Protocols such as ms-settings: are owned by packaged apps, so they need the same
        // host before they can put a window on screen.
        if (IsProtocolUri(target))
            EnsureImmersiveShell();

        Execute(target, null);
    }

    /// <summary>
    /// Starts the immersive shell if nothing else has, and waits briefly for it to come up.
    ///
    /// This is the difference between a Store app that launches and one the user can actually
    /// see. Without ShellExperienceHost running, a true UWP app - Settings, the Store - starts
    /// its process, fails to get an ApplicationFrameWindow, and sits there invisibly: the
    /// symptom is a click that appears to do nothing at all.
    ///
    /// Packaged *Win32* apps (Notepad, Terminal) are unaffected - they own an ordinary HWND
    /// and never needed a frame - which is what makes this failure so confusing to diagnose.
    ///
    /// Done lazily rather than at startup on purpose. It costs roughly 15 MB of private commit
    /// across ShellExperienceHost and ApplicationFrameHost, and a session that never opens a
    /// Store app should not pay for it - that memory is most of the point of this project.
    /// </summary>
    public static void EnsureImmersiveShell()
    {
        if (_immersiveShellChecked)
            return;

        _immersiveShellChecked = true;

        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("ShellExperienceHost").Length > 0)
                return;

            if (!ActivatePackaged(ImmersiveShellAppId))
                return;

            // Activation returns as soon as the process is created, but the app that follows
            // needs the frame infrastructure actually registered. Poll rather than sleep a
            // fixed amount, so the common case costs a few milliseconds.
            for (int i = 0; i < 40; i++)
            {
                if (System.Diagnostics.Process.GetProcessesByName("ApplicationFrameHost").Length > 0)
                    return;

                Thread.Sleep(50);
            }
        }
        catch
        {
            // Never let this stop the launch that asked for it: the app may still appear, and
            // if it does not, an invisible app is no worse than a failed one.
        }
    }

    /// <summary>Runs an executable with arguments. Plain exes never needed the routing above.</summary>
    public static void Open(string target, string? arguments, int showCmd = SW_SHOW) =>
        Execute(target, arguments, showCmd);

    /// <summary>
    /// Packaged AUMIDs are "PackageFamilyName!AppId". Legacy AppsFolder entries have no '!' -
    /// they are ordinary shortcuts that ShellExecute still opens perfectly well, and handing
    /// one to the activation manager would only fail.
    /// </summary>
    public static bool IsPackagedAppId(string id) => id.Contains('!');

    /// <summary>
    /// Activates a packaged app by AUMID. Returns false on any failure so the caller can fall
    /// back rather than leaving the user with a click that did nothing.
    /// </summary>
    public static bool ActivatePackaged(string appUserModelId)
    {
        IntPtr manager = IntPtr.Zero;

        try
        {
            Guid clsid = ClsidApplicationActivationManager;
            Guid iid = IidApplicationActivationManager;

            IntPtr ppv;
            if (Win32.CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iid, &ppv) != 0 || ppv == IntPtr.Zero)
                return false;

            manager = ppv;

            // Without this the activated app opens *behind* whatever is focused: the right to
            // set the foreground window currently belongs to us and has to be handed over.
            Win32.CoAllowSetForegroundWindow(manager, IntPtr.Zero);

            fixed (char* id = appUserModelId)
            {
                uint pid;
                return ActivateApplication(manager, id, null, AO_NONE, &pid) == 0;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (manager != IntPtr.Zero)
                Release(manager);
        }
    }

    private static void Execute(string target, string? arguments, int showCmd = SW_SHOW)
    {
        IntPtr result = Win32.ShellExecute(IntPtr.Zero, null, target, arguments, null, showCmd);

        if ((long)result > ShellExecuteMaxError)
            return;

        // ms-settings: and the other protocols owned by packaged apps land here. Explorer can
        // still dispatch a protocol even when it is not the shell, so it serves as the
        // fallback - it costs a process launch, which is why it is not the default path.
        //
        // Deliberately protocols only. Handing Explorer something it cannot dispatch does not
        // fail quietly: it leaves a stray explorer.exe sitting on a modal "OK" dialog that
        // outlives the click and has to be dismissed by hand. A file that ShellExecute has
        // already refused will not open any better for Explorer, so there is nothing to gain
        // by asking twice.
        if (IsProtocolUri(target))
            Win32.ShellExecute(IntPtr.Zero, null, "explorer.exe", Quote(target), null, showCmd);
    }

    /// <summary>
    /// True for "scheme:rest" targets such as ms-settings: or http:. Drive-qualified paths
    /// like C:\Users match the same shape and are excluded explicitly.
    /// </summary>
    private static bool IsProtocolUri(string target)
    {
        int colon = target.IndexOf(':');

        // A single leading letter before the colon is a drive, not a scheme.
        if (colon < 2)
            return false;

        for (int i = 0; i < colon; i++)
        {
            char c = target[i];
            bool valid = char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-';

            if (!valid)
                return false;
        }

        return char.IsAsciiLetter(target[0]);
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    // ---- IApplicationActivationManager, by vtable slot ----------------------------------
    // Raw slots rather than [ComImport] for the same reason as AppList: generated COM
    // interop is not AOT-clean, and the analyzers in the csproj enforce that.

    private static uint Release(IntPtr obj)
    {
        var fn = (delegate* unmanaged<IntPtr, uint>)(*(void***)obj)[2];
        return fn(obj);
    }

    private static int ActivateApplication(
        IntPtr manager, char* appUserModelId, char* arguments, uint options, uint* processId)
    {
        var fn = (delegate* unmanaged<IntPtr, char*, char*, uint, uint*, int>)(*(void***)manager)[3];
        return fn(manager, appUserModelId, arguments, options, processId);
    }
}
