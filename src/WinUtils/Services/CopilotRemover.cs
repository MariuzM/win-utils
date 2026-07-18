using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace WinOsUtils.Services;

public sealed class CopilotRemover
{
    private const string WindowsCopilotPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot";
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";

    private const string DiscoverInstalled =
        "$fams = @(Get-StartApps | Where-Object { $_.Name -like '*Copilot*' } | ForEach-Object { ($_.AppID -split '!')[0] }); "
        + "$pkgs = Get-AppxPackage -AllUsers | Where-Object { $_.Name -like '*Copilot*' -or $fams -contains $_.PackageFamilyName }; ";

    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private readonly List<Rule> _rules;

    public CopilotRemover()
    {
        _rules = BuildRules();
    }

    public List<RemediationCheck> Scan()
    {
        var results = new List<RemediationCheck>();
        foreach (var rule in _rules)
        {
            CheckState state;
            string detail;
            try
            {
                (state, detail) = rule.Evaluate();
            }
            catch (Exception e)
            {
                state = CheckState.Error;
                detail = e.Message;
            }

            results.Add(
                new RemediationCheck
                {
                    Category = rule.Category,
                    Title = rule.Title,
                    State = state,
                    Detail = detail,
                }
            );
        }
        return results;
    }

    public void Apply()
    {
        foreach (var rule in _rules)
        {
            CheckState state;
            try
            {
                (state, _) = rule.Evaluate();
            }
            catch
            {
                continue;
            }

            if (state != CheckState.NeedsChange)
                continue;

            try
            {
                rule.Remediate();
            }
            catch
            {
                // A failed remediation surfaces on the next scan.
            }
        }
    }

    private List<Rule> BuildRules()
    {
        return new List<Rule>
        {
            new()
            {
                Category = "App",
                Title = "Copilot app is uninstalled (all users)",
                Evaluate = () =>
                {
                    var found = RunPowerShell(
                        DiscoverInstalled
                            + "$out = foreach ($p in $pkgs) { $s = Get-StartApps | Where-Object { ($_.AppID -split '!')[0] -eq $p.PackageFamilyName } | Select-Object -First 1; if ($s) { $s.Name } else { $p.Name } }; "
                            + "@($out | Select-Object -Unique) -join ', '"
                    );
                    return string.IsNullOrWhiteSpace(found)
                        ? (CheckState.Compliant, "No Copilot-branded app installed")
                        : (CheckState.NeedsChange, found);
                },
                Remediate = () =>
                    RunPowerShell(
                        DiscoverInstalled + "$pkgs | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue",
                        180000
                    ),
            },
            new()
            {
                Category = "App",
                Title = "Copilot removed for new users (provisioned)",
                Evaluate = () =>
                {
                    var names = RunPowerShell(
                        DiscoverInstalled
                            + "$targets = @($pkgs | ForEach-Object Name); "
                            + "@(Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like '*Copilot*' -or $targets -contains $_.DisplayName } | ForEach-Object DisplayName | Select-Object -Unique) -join ', '"
                    );
                    return string.IsNullOrWhiteSpace(names)
                        ? (CheckState.Compliant, "Not provisioned")
                        : (CheckState.NeedsChange, names);
                },
                Remediate = () =>
                    RunPowerShell(
                        DiscoverInstalled
                            + "$targets = @($pkgs | ForEach-Object Name); "
                            + "Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like '*Copilot*' -or $targets -contains $_.DisplayName } | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue",
                        180000
                    ),
            },
            new()
            {
                Category = "Policy",
                Title = "Windows Copilot turned off (per user)",
                Evaluate = () =>
                    GetDword(RegistryHive.CurrentUser, RegistryView.Registry64, WindowsCopilotPolicy, "TurnOffWindowsCopilot")
                    == 1
                        ? (CheckState.Compliant, "TurnOffWindowsCopilot = 1")
                        : (CheckState.NeedsChange, "Policy not set"),
                Remediate = () =>
                    SetDword(
                        RegistryHive.CurrentUser,
                        RegistryView.Registry64,
                        WindowsCopilotPolicy,
                        "TurnOffWindowsCopilot",
                        1
                    ),
            },
            new()
            {
                Category = "Policy",
                Title = "Windows Copilot turned off (machine)",
                Evaluate = () =>
                    GetDword(RegistryHive.LocalMachine, RegistryView.Registry64, WindowsCopilotPolicy, "TurnOffWindowsCopilot")
                    == 1
                        ? (CheckState.Compliant, "TurnOffWindowsCopilot = 1")
                        : (CheckState.NeedsChange, "Policy not set"),
                Remediate = () =>
                    SetDword(
                        RegistryHive.LocalMachine,
                        RegistryView.Registry64,
                        WindowsCopilotPolicy,
                        "TurnOffWindowsCopilot",
                        1
                    ),
            },
            new()
            {
                Category = "Policy",
                Title = "Copilot app removal enforced",
                Evaluate = () =>
                    GetDword(RegistryHive.LocalMachine, RegistryView.Registry64, WindowsCopilotPolicy, "RemoveMicrosoftCopilotApp")
                    == 1
                        ? (CheckState.Compliant, "RemoveMicrosoftCopilotApp = 1")
                        : (CheckState.NeedsChange, "Policy not set"),
                Remediate = () =>
                    SetDword(
                        RegistryHive.LocalMachine,
                        RegistryView.Registry64,
                        WindowsCopilotPolicy,
                        "RemoveMicrosoftCopilotApp",
                        1
                    ),
            },
            new()
            {
                Category = "Taskbar",
                Title = "Copilot button hidden from taskbar",
                Evaluate = () =>
                    GetDword(RegistryHive.CurrentUser, RegistryView.Registry64, ExplorerAdvanced, "ShowCopilotButton")
                    == 1
                        ? (CheckState.NeedsChange, "Button shown on taskbar")
                        : (CheckState.Compliant, "Hidden"),
                Remediate = () =>
                    SetDword(RegistryHive.CurrentUser, RegistryView.Registry64, ExplorerAdvanced, "ShowCopilotButton", 0),
            },
            new()
            {
                Category = "Edge",
                Title = "Copilot / sidebar disabled in Edge",
                Evaluate = () =>
                    GetDword(RegistryHive.LocalMachine, RegistryView.Registry64, EdgePolicy, "HubsSidebarEnabled") == 0
                        ? (CheckState.Compliant, "Edge sidebar disabled")
                        : (CheckState.NeedsChange, "Edge Copilot sidebar available"),
                Remediate = () =>
                    SetDword(RegistryHive.LocalMachine, RegistryView.Registry64, EdgePolicy, "HubsSidebarEnabled", 0),
            },
        };
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

    private static int? GetDword(RegistryHive hive, RegistryView view, string sub, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(sub, false);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static void SetDword(RegistryHive hive, RegistryView view, string sub, string name, int value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.CreateSubKey(sub, true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}
