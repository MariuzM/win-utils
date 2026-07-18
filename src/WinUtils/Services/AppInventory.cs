using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinUtils.Services;

public enum AppSource
{
    Win32,
    Store,
}

public enum LeftoverKind
{
    Folder,
    RegistryKey,
}

// A residual file folder or registry key that an app tends to leave behind after uninstalling.
public sealed class AppLeftover
{
    public LeftoverKind Kind { get; init; }
    public string Location { get; init; } = "";
    public RegistryHive Hive { get; init; }
    public string RegistrySub { get; init; } = "";
}

public sealed class InstalledApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public AppSource Source { get; init; }

    public string SourceLabel => Source == AppSource.Store ? "Store" : "Win32";

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Publisher))
                parts.Add(Publisher);
            if (!string.IsNullOrWhiteSpace(Version))
                parts.Add($"v{Version}");
            return string.Join("  •  ", parts);
        }
    }

    public string LeftoverSummary =>
        Leftovers.Count == 0 ? "No extra files detected" : $"{Leftovers.Count} leftover location(s) will also be removed";

    // Uninstall data — one of these is used depending on Source.
    public string? QuietUninstall { get; init; }
    public string? Uninstall { get; init; }
    public string? PackageFullName { get; init; }
    public string? InstallLocation { get; init; }

    // Set for Win32 apps so the leftover uninstall-registry key can be confirmed / removed.
    public RegistryHive UninstallHive { get; init; }
    public string UninstallKeySub { get; init; } = "";

    public List<AppLeftover> Leftovers { get; init; } = new();

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record AppActionResult(bool Ok, string Name, string Message);

public sealed class AppInventory
{
    private static readonly string ProgramFiles =
        Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
    private static readonly string ProgramFilesX86 =
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string ProgramData =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static readonly string[] DataRoots =
    {
        ProgramFiles,
        ProgramFilesX86,
        LocalAppData,
        RoamingAppData,
        ProgramData,
        Path.Combine(LocalAppData, "Programs"),
    };

    // Never treat these as an app's private folder, even if a name/publisher happens to match.
    private static readonly HashSet<string> ProtectedRoots = BuildProtectedRoots();

    // Generic tokens too common to safely match a leftover folder/key against.
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "update", "updater", "tool", "tools", "driver", "drivers", "runtime",
        "setup", "install", "installer", "common", "shared", "data", "cache", "temp", "bin",
        "microsoft", "windows", "google", "intel", "nvidia", "amd", "realtek", "system",
    };

    public List<InstalledApp> Scan()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in ScanWin32())
            if (seen.Add($"{app.Name}|{app.Version}"))
                apps.Add(app);

        foreach (var app in ScanStore())
            if (seen.Add($"{app.Name}|store"))
                apps.Add(app);

        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AppActionResult Uninstall(InstalledApp app)
    {
        try
        {
            if (app.Source == AppSource.Store)
                RemoveStore(app.PackageFullName);
            else
                RemoveWin32(app);
        }
        catch (Exception e)
        {
            return new AppActionResult(false, app.Name, $"Uninstall failed: {e.Message}");
        }

        var removed = 0;
        foreach (var leftover in app.Leftovers)
            if (TryRemoveLeftover(leftover))
                removed++;

        if (IsStillInstalled(app))
            return new AppActionResult(
                false,
                app.Name,
                "The uninstaller didn't finish — it may need user interaction or a reboot."
            );

        return new AppActionResult(
            true,
            app.Name,
            removed > 0 ? $"Uninstalled; cleaned {removed} leftover location(s)." : "Uninstalled."
        );
    }

    // ---- Win32 (classic "Programs and Features") ----

    private IEnumerable<InstalledApp> ScanWin32()
    {
        var sources = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach (var (hive, view) in sources)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                false
            );
            if (uninstall == null)
                continue;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                InstalledApp? app = null;
                try
                {
                    using var key = uninstall.OpenSubKey(subName, false);
                    app = ReadWin32(key, hive, view, subName);
                }
                catch
                {
                    // Skip unreadable entries rather than aborting the whole scan.
                }

                if (app != null)
                    yield return app;
            }
        }
    }

    private InstalledApp? ReadWin32(RegistryKey? key, RegistryHive hive, RegistryView view, string subName)
    {
        if (key == null)
            return null;

        var name = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Skip system components, patches and update entries — not user-facing apps.
        if ((key.GetValue("SystemComponent") as int?) == 1)
            return null;
        if (!string.IsNullOrWhiteSpace(key.GetValue("ParentKeyName") as string))
            return null;
        var releaseType = key.GetValue("ReleaseType") as string ?? "";
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Regex.IsMatch(name, @"^KB\d{6,}"))
            return null;

        var quiet = (key.GetValue("QuietUninstallString") as string)?.Trim();
        var uninstall = (key.GetValue("UninstallString") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(quiet) && string.IsNullOrWhiteSpace(uninstall))
            return null; // Nothing we can invoke to remove it.

        var publisher = (key.GetValue("Publisher") as string)?.Trim() ?? "";
        var version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? "";
        var installLocation = ((key.GetValue("InstallLocation") as string) ?? "").Trim().Trim('"');

        var keySub = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{subName}";
        if (view == RegistryView.Registry32)
            keySub = $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{subName}";

        var leftovers = DetectLeftovers(name, installLocation, AppSource.Win32, null);
        // The uninstall registry key itself is a guaranteed leftover if the uninstaller leaves it.
        leftovers.Add(new AppLeftover { Kind = LeftoverKind.RegistryKey, Location = $"{HiveLabel(hive)}\\{keySub}", Hive = hive, RegistrySub = keySub });

        return new InstalledApp
        {
            Name = name,
            Publisher = publisher,
            Version = version,
            Source = AppSource.Win32,
            QuietUninstall = string.IsNullOrWhiteSpace(quiet) ? null : quiet,
            Uninstall = string.IsNullOrWhiteSpace(uninstall) ? null : uninstall,
            InstallLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation,
            UninstallHive = hive,
            UninstallKeySub = keySub,
            Leftovers = DedupeLeftovers(leftovers),
        };
    }

    private void RemoveWin32(InstalledApp app)
    {
        string command;
        if (!string.IsNullOrWhiteSpace(app.QuietUninstall))
        {
            command = app.QuietUninstall!;
        }
        else
        {
            command = app.Uninstall ?? "";
            if (command.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // MSI: force removal and run it unattended.
                command = Regex.Replace(command, "/I", "/X", RegexOptions.IgnoreCase);
                if (command.IndexOf("/qn", StringComparison.OrdinalIgnoreCase) < 0
                    && command.IndexOf("/quiet", StringComparison.OrdinalIgnoreCase) < 0)
                    command += " /qn /norestart";
            }
            // Non-MSI uninstallers without a quiet string may still show their own UI.
        }

        if (string.IsNullOrWhiteSpace(command))
            return;

        RunCommand(command, 600000);
    }

    // ---- Store / packaged apps (only those the user sees in Start) ----

    private IEnumerable<InstalledApp> ScanStore()
    {
        var raw = RunPowerShell(
            "$start = @(Get-StartApps | ForEach-Object { ($_.AppID -split '!')[0] }); "
                + "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and "
                + "$start -contains $_.PackageFamilyName } | ForEach-Object { "
                + "\"$($_.Name)`t$($_.PackageFullName)`t$($_.PackageFamilyName)\" }",
            120000
        );

        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var line in raw.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
                continue;

            var name = parts[0];
            var packageFullName = parts[1];
            var family = parts[2];

            var publisher = name.Contains('.') ? name[..name.IndexOf('.')] : "";
            var display = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

            var leftovers = new List<AppLeftover>();
            var dataDir = Path.Combine(LocalAppData, "Packages", family);
            if (Directory.Exists(dataDir))
                leftovers.Add(new AppLeftover { Kind = LeftoverKind.Folder, Location = dataDir });

            yield return new InstalledApp
            {
                Name = display,
                Publisher = publisher,
                Source = AppSource.Store,
                PackageFullName = packageFullName,
                Leftovers = leftovers,
            };
        }
    }

    private void RemoveStore(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
            return;

        RunPowerShell(
            $"Remove-AppxPackage -Package '{packageFullName}' -ErrorAction SilentlyContinue",
            180000
        );
    }

    private bool IsStillInstalled(InstalledApp app)
    {
        if (app.Source == AppSource.Store)
        {
            var found = RunPowerShell(
                "@(Get-AppxPackage | Where-Object { $_.PackageFullName -eq "
                    + $"'{EscapeSingle(app.PackageFullName ?? "")}' }}).Count",
                60000
            );
            var trimmed = found.Trim();
            return !string.IsNullOrWhiteSpace(trimmed) && trimmed != "0";
        }

        using var baseKey = RegistryKey.OpenBaseKey(
            app.UninstallHive,
            app.UninstallKeySub.Contains("WOW6432Node") ? RegistryView.Registry32 : RegistryView.Registry64
        );
        using var key = baseKey.OpenSubKey(app.UninstallKeySub, false);
        // If the uninstall key is gone (or we already deleted it as a leftover), treat as removed.
        return key != null && key.GetValue("DisplayName") is string;
    }

    // ---- Leftover detection & removal ----

    private List<AppLeftover> DetectLeftovers(string name, string installLocation, AppSource source, string? packageFamily)
    {
        var list = new List<AppLeftover>();

        if (!string.IsNullOrWhiteSpace(installLocation)
            && Directory.Exists(installLocation)
            && !IsProtectedPath(installLocation))
        {
            list.Add(new AppLeftover { Kind = LeftoverKind.Folder, Location = installLocation });
        }

        var clean = CleanName(name);
        if (IsSpecific(clean))
        {
            foreach (var root in DataRoots)
            {
                var candidate = Path.Combine(root, clean);
                if (Directory.Exists(candidate) && !IsProtectedPath(candidate))
                    list.Add(new AppLeftover { Kind = LeftoverKind.Folder, Location = candidate });
            }

            foreach (var (hive, sub) in new[]
            {
                (RegistryHive.CurrentUser, $@"Software\{clean}"),
                (RegistryHive.LocalMachine, $@"SOFTWARE\{clean}"),
                (RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\{clean}"),
            })
            {
                if (RegistryKeyExists(hive, sub))
                    list.Add(new AppLeftover
                    {
                        Kind = LeftoverKind.RegistryKey,
                        Location = $"{HiveLabel(hive)}\\{sub}",
                        Hive = hive,
                        RegistrySub = sub,
                    });
            }
        }

        return list;
    }

    private bool TryRemoveLeftover(AppLeftover leftover)
    {
        try
        {
            if (leftover.Kind == LeftoverKind.Folder)
            {
                if (Directory.Exists(leftover.Location) && !IsProtectedPath(leftover.Location))
                {
                    Directory.Delete(leftover.Location, true);
                    return true;
                }
                return false;
            }

            if (RegistryKeyExists(leftover.Hive, leftover.RegistrySub))
            {
                using var baseKey = RegistryKey.OpenBaseKey(leftover.Hive, RegistryView.Registry64);
                baseKey.DeleteSubKeyTree(leftover.RegistrySub, false);
                return true;
            }
        }
        catch
        {
            // Locked file or protected key — leave it; the user can retry after a reboot.
        }
        return false;
    }

    private static List<AppLeftover> DedupeLeftovers(List<AppLeftover> leftovers) =>
        leftovers
            .GroupBy(l => l.Location, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    // ---- Helpers ----

    private static string CleanName(string name)
    {
        var s = Regex.Replace(name, @"\(.*?\)", " "); // parentheticals
        s = Regex.Replace(s, @"\d+(\.\d+)+", " "); // version numbers
        s = Regex.Replace(s, @"\b(x64|x86|64-bit|32-bit|bit)\b", " ", RegexOptions.IgnoreCase);
        s = s.Replace("™", "").Replace("®", "").Replace("©", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static bool IsSpecific(string clean) =>
        clean.Length >= 4 && !GenericNames.Contains(clean) && !Regex.IsMatch(clean, @"^\d+$");

    private static bool IsProtectedPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return true;
        }

        if (full.Length <= 3) // e.g. "C:\"
            return true;

        // Reject anything directly at a drive root or one of the known shared roots.
        return ProtectedRoots.Contains(full);
    }

    private static HashSet<string> BuildProtectedRoots()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p))
                set.Add(p!.TrimEnd('\\'));
        }

        Add(ProgramFiles);
        Add(ProgramFilesX86);
        Add(Environment.GetEnvironmentVariable("ProgramW6432"));
        Add(LocalAppData);
        Add(RoamingAppData);
        Add(ProgramData);
        Add(Path.Combine(LocalAppData, "Programs"));
        Add(Path.Combine(LocalAppData, "Packages"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Path.Combine(RoamingAppData, "Microsoft"));
        Add(Path.Combine(LocalAppData, "Microsoft"));
        Add(Path.Combine(ProgramData, "Microsoft"));
        Add(@"C:\Users");
        return set;
    }

    private static bool RegistryKeyExists(RegistryHive hive, string sub)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(sub, false);
        return key != null;
    }

    private static string HiveLabel(RegistryHive hive) =>
        hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    private static string EscapeSingle(string s) => s.Replace("'", "''");

    private static void RunCommand(string command, int waitMs)
    {
        var info = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(info);
        if (p == null)
            return;
        p.WaitForExit(waitMs);
    }

    private static string RunPowerShell(string script, int waitMs = 60000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = Process.Start(info);
        if (p == null)
            return "";

        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(waitMs);
        return output.Trim();
    }
}
