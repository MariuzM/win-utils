using System.Text;
using Microsoft.Win32;

namespace WinShell;

internal static class SearchControl
{
    private const string ExplorerPolicy = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string SearchSettings = @"Software\Microsoft\Windows\CurrentVersion\Search";

    public static string Status()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"SearchHost.exe running    : {Running("SearchHost")}");
        sb.AppendLine($"SearchIndexer.exe running : {Running("SearchIndexer")}");
        sb.AppendLine($"WSearch service           : {ServiceState()}");
        sb.AppendLine($"DisableSearchBoxSuggestions: {ReadDword(Registry.CurrentUser, ExplorerPolicy, "DisableSearchBoxSuggestions")}");
        sb.AppendLine($"BingSearchEnabled          : {ReadDword(Registry.CurrentUser, SearchSettings, "BingSearchEnabled")}");
        sb.Append($"CortanaConsent             : {ReadDword(Registry.CurrentUser, SearchSettings, "CortanaConsent")}");

        return sb.ToString();
    }

    public static bool Disable(out string message)
    {
        var sb = new StringBuilder();

        WriteDword(SearchSettings, "BingSearchEnabled", 0, sb);
        WriteDword(SearchSettings, "CortanaConsent", 0, sb);
        sb.AppendLine(Kill("SearchHost"));
        sb.AppendLine();

        if (ServiceState().StartsWith("Disabled", StringComparison.Ordinal))
        {
            sb.AppendLine("Indexing (WSearch) is already disabled.");
        }
        else
        {
            sb.AppendLine("Requesting elevation to disable the WSearch indexing service...");
            sb.AppendLine("Accept the UAC prompt. Nothing is changed if you decline.");
            Elevate("Stop-Service WSearch -Force; Set-Service WSearch -StartupType Disabled");
        }

        sb.AppendLine();
        sb.AppendLine("About SearchHost.exe: it belongs to Explorer, which relaunches it on demand,");
        sb.AppendLine("so killing it only frees memory until the next time something opens search.");
        sb.Append("It does not run at all once WinShell is the shell (--install-shell).");

        message = sb.ToString();
        return true;
    }

    public static bool Enable(out string message)
    {
        var sb = new StringBuilder();

        WriteDword(SearchSettings, "BingSearchEnabled", 1, sb);
        sb.AppendLine();
        sb.AppendLine("Requesting elevation to re-enable the WSearch indexing service...");
        Elevate("Set-Service WSearch -StartupType Automatic; Start-Service WSearch");

        message = sb.ToString();
        return true;
    }

    private static void Elevate(string command)
    {
        Native.Win32.ShellExecute(
            IntPtr.Zero, "runas", "powershell.exe",
            $"-NoProfile -WindowStyle Hidden -Command \"{command}\"", null, 0);
    }

    private static string Running(string name)
    {
        try
        {
            System.Diagnostics.Process[] found = System.Diagnostics.Process.GetProcessesByName(name);
            long bytes = 0;

            foreach (System.Diagnostics.Process p in found)
            {
                bytes += p.WorkingSet64;
                p.Dispose();
            }

            return found.Length == 0 ? "no" : $"yes ({bytes / 1024 / 1024} MB)";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Kill(string name)
    {
        try
        {
            System.Diagnostics.Process[] found = System.Diagnostics.Process.GetProcessesByName(name);

            if (found.Length == 0)
                return $"{name}: not running";

            int killed = 0;
            foreach (System.Diagnostics.Process p in found)
            {
                try
                {
                    p.Kill();
                    killed++;
                }
                catch
                {
                }

                p.Dispose();
            }

            return killed > 0
                ? $"{name}: terminated {killed}"
                : $"{name}: could not terminate (needs elevation)";
        }
        catch
        {
            return $"{name}: could not terminate";
        }
    }

    private static string ServiceState()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WSearch");

            if (key == null)
                return "not installed";

            return (key.GetValue("Start") as int?) switch
            {
                4 => "Disabled",
                3 => "Manual",
                2 => "Automatic",
                _ => "unknown",
            };
        }
        catch
        {
            return "unreadable";
        }
    }

    private static void WriteDword(string path, string name, int value, StringBuilder sb)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, true);
            key.SetValue(name, value, RegistryValueKind.DWord);
            sb.AppendLine($"{name} = {value}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{name}: failed ({ex.Message})");
        }
    }

    private static string ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            object? value = key?.GetValue(name);
            return value == null ? "<not set>" : value.ToString() ?? "<not set>";
        }
        catch
        {
            return "<unreadable>";
        }
    }
}
