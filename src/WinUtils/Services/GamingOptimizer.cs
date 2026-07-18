using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace WinUtils.Services;

public sealed class ProcessSummary
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string Instances => Count > 1 ? $"×{Count}" : "";
    public string Memory { get; init; } = "";
}

public sealed class GamingOptimizer
{
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";
    private const string DshPolicy = @"SOFTWARE\Policies\Microsoft\Dsh";
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string WidgetsPackage = "MicrosoftWindows.Client.WebExperience";

    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private static readonly (string Service, string Title, string Category)[] Services =
    {
        ("DiagTrack", "Connected User Experiences and Telemetry", "Telemetry"),
        ("dmwappushservice", "Device Management WAP Push", "Telemetry"),
        ("WerSvc", "Windows Error Reporting", "Telemetry"),
        ("Spooler", "Print Spooler", "Printing"),
        ("Fax", "Fax", "Printing"),
        ("MapsBroker", "Downloaded Maps Manager", "Bloat"),
        ("RetailDemo", "Retail Demo Service", "Bloat"),
        ("WMPNetworkSvc", "Windows Media Player Network Sharing", "Bloat"),
        ("RemoteRegistry", "Remote Registry", "Security"),
    };

    private readonly List<Rule> _rules;

    public GamingOptimizer()
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
            }
        }
    }

    public List<ProcessSummary> ScanProcesses()
    {
        return Process
            .GetProcesses()
            .GroupBy(p => p.ProcessName)
            .Select(g =>
            {
                long bytes = 0;
                foreach (var p in g)
                {
                    try
                    {
                        bytes += p.WorkingSet64;
                    }
                    catch
                    {
                    }
                }

                return new ProcessSummary
                {
                    Name = g.Key,
                    Count = g.Count(),
                    Memory = FormatSize(bytes),
                };
            })
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Name)
            .ToList();
    }

    private List<Rule> BuildRules()
    {
        var rules = new List<Rule>();

        foreach (var s in Services)
        {
            var service = s.Service;
            rules.Add(
                new Rule
                {
                    Category = s.Category,
                    Title = s.Title,
                    Evaluate = () => EvaluateService(service),
                    Remediate = () => DisableService(service),
                }
            );
        }

        rules.Add(
            new Rule
            {
                Category = "Widgets",
                Title = "Windows Widgets turned off (policy)",
                Evaluate = () =>
                    GetDword(RegistryHive.LocalMachine, DshPolicy, "AllowNewsAndInterests") == 0
                        ? (CheckState.Compliant, "Disabled by policy")
                        : (CheckState.NeedsChange, "Widgets is enabled"),
                Remediate = () => SetDword(RegistryHive.LocalMachine, DshPolicy, "AllowNewsAndInterests", 0),
            }
        );

        rules.Add(
            new Rule
            {
                Category = "Widgets",
                Title = "Widgets button hidden from taskbar",
                Evaluate = () =>
                    GetDword(RegistryHive.CurrentUser, ExplorerAdvanced, "TaskbarDa") == 0
                        ? (CheckState.Compliant, "Hidden")
                        : (CheckState.NeedsChange, "Button shown on taskbar"),
                Remediate = () => SetDword(RegistryHive.CurrentUser, ExplorerAdvanced, "TaskbarDa", 0),
            }
        );

        rules.Add(
            new Rule
            {
                Category = "Widgets",
                Title = "Widgets app removed",
                Evaluate = () =>
                {
                    var count = RunPowerShell($"@(Get-AppxPackage -AllUsers -Name '{WidgetsPackage}').Count");
                    return int.TryParse(count, out var c) && c > 0
                        ? (CheckState.NeedsChange, "Installed — spawns msedgewebview2")
                        : (CheckState.Compliant, "Not installed");
                },
                Remediate = () =>
                    RunPowerShell(
                        $"Get-AppxPackage -AllUsers -Name '{WidgetsPackage}' | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue; "
                            + $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq '{WidgetsPackage}' }} | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue",
                        120000
                    ),
            }
        );

        return rules;
    }

    private static (CheckState State, string Detail) EvaluateService(string service)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{service}", false);
        if (key == null)
            return (CheckState.NotApplicable, $"Not installed — {service}");

        var start = key.GetValue("Start") as int?;
        if (start == 4)
            return (CheckState.Compliant, $"Disabled — {service}");

        var mode = start switch
        {
            0 => "Boot",
            1 => "System",
            2 => "Automatic",
            3 => "Manual",
            _ => "Enabled",
        };
        return (CheckState.NeedsChange, $"{mode} — {service}");
    }

    private static void DisableService(string service)
    {
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{service}", true))
        {
            key?.SetValue("Start", 4, RegistryValueKind.DWord);
        }

        RunSc($"stop {service}");
    }

    private static int? GetDword(RegistryHive hive, string sub, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(sub, false);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static void SetDword(RegistryHive hive, string sub, string name, int value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(sub, true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void RunSc(string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(info);
        p?.WaitForExit(15000);
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

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return $"{size:0.#} {units[i]}";
    }
}
