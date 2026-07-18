using Microsoft.Win32;

namespace WinShell;

internal static class ShellRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string ValueName = "Shell";

    public static string CurrentUserShell()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string ?? string.Empty;
    }

    public static string MachineShell()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string ?? string.Empty;
    }

    public static string InstalledPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinShell", "WinShell.exe");

    public static bool IsInstalled => CurrentUserShell().Length > 0;

    /// <summary>
    /// Copies the running build to %LOCALAPPDATA%\WinShell and registers *that* copy as the
    /// shell. The login shell starts before network shares are mounted, so registering a build
    /// that lives on the Mac share would guarantee a blank screen; copying first is what makes
    /// installing from there safe. A stable local copy also survives rebuilding or deleting the
    /// source tree.
    /// </summary>
    public static bool InstallToLocal(out string message)
    {
        string source = Environment.ProcessPath ?? string.Empty;
        string? sourceDir = Path.GetDirectoryName(source);

        if (source.Length == 0 || sourceDir == null)
        {
            message = "Could not determine the running executable path.";
            return false;
        }

        string targetDir = Path.GetDirectoryName(InstalledPath)!;

        try
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, Path.Combine(targetDir, name), true);
            }

            string copiedExe = Path.Combine(targetDir, Path.GetFileName(source));
            if (!File.Exists(copiedExe))
            {
                message = $"Copy succeeded but {Path.GetFileName(source)} is missing from {targetDir}.";
                return false;
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            key.SetValue(ValueName, $"\"{copiedExe}\" --tray", RegistryValueKind.String);

            message =
                $"Installed to {targetDir}{Environment.NewLine}" +
                $"WinShell will start as your shell at next sign-in.";

            return true;
        }
        catch (Exception ex)
        {
            message = $"Install failed: {ex.Message}";
            return false;
        }
    }

    public static bool Install(out string message)
    {
        string exe = Environment.ProcessPath ?? string.Empty;

        if (exe.Length == 0)
        {
            message = "Could not determine the executable path.";
            return false;
        }

        if (exe.StartsWith(@"\\", StringComparison.Ordinal))
        {
            message =
                $"Refusing to install: WinShell is running from a network path.{Environment.NewLine}" +
                $"  {exe}{Environment.NewLine}{Environment.NewLine}" +
                $"The login shell starts before network shares are mounted, so this would leave{Environment.NewLine}" +
                $"you at a blank screen with no shell at next sign-in.{Environment.NewLine}{Environment.NewLine}" +
                $"Copy the build to a local disk and run --install-shell from there instead.";

            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            key.SetValue(ValueName, $"\"{exe}\" --tray", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            message = $"Failed to write the shell value: {ex.Message}";
            return false;
        }

        message =
            $"WinShell is now the shell for {Environment.UserName}.{Environment.NewLine}" +
            $"  value: \"{exe}\" --tray{Environment.NewLine}{Environment.NewLine}" +
            $"Takes effect at next sign-in. Explorer will no longer create a taskbar,{Environment.NewLine}" +
            $"which is what lets WinShell own the notification area.{Environment.NewLine}{Environment.NewLine}" +
            $"WinShell draws its own desktop - wallpaper, icons and right-click menus - because{Environment.NewLine}" +
            $"Explorer will no longer create one.{Environment.NewLine}{Environment.NewLine}" +
            $"RECOVERY, if WinShell fails to start and you are left with a blank screen:{Environment.NewLine}" +
            $"  1. Ctrl+Shift+Esc opens Task Manager - it works with no shell running.{Environment.NewLine}" +
            $"  2. File > Run new task > explorer.exe  gets the desktop back immediately.{Environment.NewLine}" +
            $"  3. Then run:  \"{exe}\" --uninstall-shell   to undo this permanently.";

        return true;
    }

    public static bool Uninstall(out string message)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            key?.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            message = $"Failed to clear the shell value: {ex.Message}";
            return false;
        }

        message =
            $"Per-user shell override removed. Windows falls back to the machine shell{Environment.NewLine}" +
            $"(\"{MachineShell()}\") at next sign-in.";

        return true;
    }

    public static string Status()
    {
        string user = CurrentUserShell();

        return
            $"machine shell (HKLM): {MachineShell()}{Environment.NewLine}" +
            $"user shell    (HKCU): {(user.Length == 0 ? "<not set - Explorer is the shell>" : user)}";
    }
}
