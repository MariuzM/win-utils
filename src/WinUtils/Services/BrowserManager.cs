using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinUtils.Services;

public enum BrowserChoice
{
    Chrome,
    Brave,
    Firefox,
}

public sealed record ActionResult(bool Ok, string Title, string Message);

public sealed record BrowserState(bool Installed, string Detail);

public sealed class BrowserManager
{
    private const string EdgeStableGuid = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";

    private static readonly string EdgeAppDir = Path.Combine(
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
        "Microsoft",
        "Edge",
        "Application"
    );

    public string? EdgeVersion()
    {
        foreach (var path in new[]
        {
            $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{EdgeStableGuid}",
            $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{EdgeStableGuid}",
        })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path, false);
            if (key?.GetValue("pv") is string pv && !string.IsNullOrWhiteSpace(pv))
                return pv;
        }
        return null;
    }

    public bool IsEdgeInstalled() =>
        EdgeVersion() is not null || File.Exists(Path.Combine(EdgeAppDir, "msedge.exe"));

    public BrowserState GetBrowserState(BrowserChoice choice)
    {
        var (exe, files) = LocateInfo(choice);
        var path = FindExe(exe, files);
        return path is null
            ? new BrowserState(false, "Not installed")
            : new BrowserState(true, "Installed");
    }

    public ActionResult RemoveEdge()
    {
        if (!IsEdgeInstalled())
            return new ActionResult(true, "Edge isn't installed", "Microsoft Edge isn't present on this PC.");

        RunPowerShell(EdgeRemovalScript, 300000);

        if (!IsEdgeInstalled())
        {
            return new ActionResult(
                true,
                "Microsoft Edge removed",
                "Edge is uninstalled and blocked from silently reinstalling through Windows Update. The WebView2 runtime was left in place so apps that rely on it keep working. Pick a browser below to install."
            );
        }

        return new ActionResult(
            false,
            "Edge couldn't be fully removed",
            "Windows is blocking Edge removal on this build or region. In the EU/EEA you can finish it from Settings › Apps › Installed apps › Microsoft Edge › Uninstall. WebView2 and its dependent apps were left untouched."
        );
    }

    public ActionResult InstallBrowser(BrowserChoice choice)
    {
        var name = DisplayName(choice);

        if (GetBrowserState(choice).Installed)
            return new ActionResult(true, $"{name} already installed", $"{name} is already on this PC.");

        var (id, machine) = choice switch
        {
            BrowserChoice.Chrome => ("Google.Chrome", true),
            BrowserChoice.Brave => ("Brave.Brave", false),
            BrowserChoice.Firefox => ("Mozilla.Firefox", true),
            _ => ("", false),
        };

        var script = InstallTemplate
            .Replace("__ID__", id)
            .Replace("__MACHINE__", machine ? "$true" : "$false");

        var output = RunPowerShell(script, 420000);

        if (output.Contains("NO_WINGET", StringComparison.OrdinalIgnoreCase))
        {
            return new ActionResult(
                false,
                "Windows Package Manager missing",
                "winget isn't available on this PC. Install \"App Installer\" from the Microsoft Store, then try again."
            );
        }

        if (GetBrowserState(choice).Installed)
        {
            return new ActionResult(
                true,
                $"{name} installed",
                $"{name} was installed with winget. You can now set it as your default browser in Settings › Apps › Default apps."
            );
        }

        return new ActionResult(
            false,
            $"Couldn't install {name}",
            "The winget install didn't complete. Check your internet connection and try again."
        );
    }

    public static string DisplayName(BrowserChoice choice) =>
        choice switch
        {
            BrowserChoice.Chrome => "Google Chrome",
            BrowserChoice.Brave => "Brave",
            BrowserChoice.Firefox => "Mozilla Firefox",
            _ => choice.ToString(),
        };

    private static (string Exe, string[] Files) LocateInfo(BrowserChoice choice)
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return choice switch
        {
            BrowserChoice.Chrome => (
                "chrome.exe",
                new[]
                {
                    Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                }
            ),
            BrowserChoice.Brave => (
                "brave.exe",
                new[]
                {
                    Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(pf86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                }
            ),
            BrowserChoice.Firefox => (
                "firefox.exe",
                new[]
                {
                    Path.Combine(pf, "Mozilla Firefox", "firefox.exe"),
                    Path.Combine(pf86, "Mozilla Firefox", "firefox.exe"),
                }
            ),
            _ => ("", Array.Empty<string>()),
        };
    }

    private static string? FindExe(string exe, string[] files)
    {
        foreach (var file in files)
            if (File.Exists(file))
                return file;

        var appPaths = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(appPaths, false);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                return path;
        }

        return null;
    }

    private const string InstallTemplate =
        "$id = '__ID__'\n"
        + "$machine = __MACHINE__\n"
        + "if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Write-Output 'NO_WINGET'; exit }\n"
        + "$a = @('install','-e','--id',$id,'--silent','--accept-source-agreements','--accept-package-agreements')\n"
        + "if ($machine) { $a += @('--scope','machine') }\n"
        + "& winget @a\n"
        + "Write-Output ('EXIT:' + $LASTEXITCODE)\n";

    private const string EdgeRemovalScript =
        "$ErrorActionPreference = 'SilentlyContinue'\n"
        + "$base = \"${env:ProgramFiles(x86)}\\Microsoft\\Edge\\Application\"\n"
        + "$setup = Get-ChildItem $base -Recurse -Filter setup.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like '*\\Installer\\setup.exe' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName\n"
        + "$uninst = 'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Microsoft Edge'\n"
        + "New-Item -Path $uninst -Force | Out-Null\n"
        + "Set-ItemProperty -Path $uninst -Name 'NoRemove' -Type DWord -Value 0\n"
        + "$geo = 'HKCU:\\Control Panel\\International\\Geo'\n"
        + "$oldName = (Get-ItemProperty $geo -Name Name -ErrorAction SilentlyContinue).Name\n"
        + "$oldNation = (Get-ItemProperty $geo -Name Nation -ErrorAction SilentlyContinue).Nation\n"
        + "Set-ItemProperty $geo -Name Name -Value 'DE'\n"
        + "Set-ItemProperty $geo -Name Nation -Value '94'\n"
        + "$pol = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\EdgeUpdate'\n"
        + "New-Item -Path $pol -Force | Out-Null\n"
        + "Set-ItemProperty $pol -Name 'Uninstall{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}' -Type DWord -Value 1\n"
        + "if ($setup) {\n"
        + "  & $setup --uninstall --system-level --verbose-logging --force-uninstall\n"
        + "  Start-Sleep -Seconds 6\n"
        + "  & $setup --uninstall --verbose-logging --force-uninstall\n"
        + "  Start-Sleep -Seconds 3\n"
        + "}\n"
        + "if ($oldName) { Set-ItemProperty $geo -Name Name -Value $oldName }\n"
        + "if ($oldNation) { Set-ItemProperty $geo -Name Nation -Value $oldNation }\n"
        + "Set-ItemProperty $pol -Name 'Install{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}' -Type DWord -Value 0\n"
        + "Set-ItemProperty $pol -Name 'Update{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}' -Type DWord -Value 0\n"
        + "Set-ItemProperty $pol -Name 'Install{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -Type DWord -Value 1\n"
        + "if (Test-Path \"$base\\msedge.exe\") { Write-Output 'STILL_PRESENT' } else { Write-Output 'REMOVED' }\n";

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

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(waitMs);
        outTask.Wait(2000);
        errTask.Wait(2000);
        return outTask.Result.Trim();
    }
}
