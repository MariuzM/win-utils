using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace WinOsUtils.Services;

// Deep scan for Microsoft Edge leftovers that survive an uninstall: files, registry,
// scheduled tasks, services, shortcuts and the Appx package. The WebView2 runtime and the
// shared updater it depends on are deliberately preserved — anything WebView2 needs is
// reported as "Kept" rather than flagged for removal, mirroring RemoveEdge().
public sealed class EdgeLeftoverScanner
{
    private const string EdgeStableGuid = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
    private const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly string ProgramFilesX86 =
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string ProgramData =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static readonly string PublicDesktop =
        Path.Combine(
            Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
            "Desktop"
        );

    private static readonly string UserDesktop =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static readonly string StartMenuPrograms =
        Path.Combine(ProgramData, @"Microsoft\Windows\Start Menu\Programs");

    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private readonly List<Rule> _rules;
    private readonly bool _webView2Present;

    public EdgeLeftoverScanner()
    {
        _webView2Present = IsWebView2Present();
        _rules = BuildRules();
    }

    public bool WebView2Present => _webView2Present;

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

    public void Clean()
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
                // A failed cleanup surfaces on the next scan.
            }
        }
    }

    private List<Rule> BuildRules()
    {
        return new List<Rule>
        {
            // ---- Files & folders (browser-specific — always removable) ----
            DirRule("Files", "Edge program files", Path.Combine(ProgramFilesX86, "Microsoft", "Edge")),
            DirRule("Files", "EdgeCore program files", Path.Combine(ProgramFilesX86, "Microsoft", "EdgeCore")),
            DirRule("Files", "Edge user profile data", Path.Combine(LocalAppData, "Microsoft", "Edge")),
            DirRule("Files", "Per-user EdgeUpdate data", Path.Combine(LocalAppData, "Microsoft", "EdgeUpdate")),

            // ---- Shared updater files (kept if WebView2 is present) ----
            UpdaterDirRule("Files", "EdgeUpdate program files", Path.Combine(ProgramFilesX86, "Microsoft", "EdgeUpdate")),
            UpdaterDirRule("Files", "EdgeUpdate app data", Path.Combine(ProgramData, "Microsoft", "EdgeUpdate")),

            // ---- Registry (browser-specific) ----
            KeyRule(
                "Registry",
                "HKLM Edge policy / settings key",
                RegistryHive.LocalMachine,
                new[] { @"SOFTWARE\Microsoft\Edge", @"SOFTWARE\WOW6432Node\Microsoft\Edge" }
            ),
            KeyRule(
                "Registry",
                "Edge uninstall entry",
                RegistryHive.LocalMachine,
                new[]
                {
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge",
                }
            ),
            KeyRule(
                "Registry",
                "msedge.exe App Paths entry",
                RegistryHive.LocalMachine,
                new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe" }
            ),
            KeyRule(
                "Registry",
                "Edge file-type handlers (ProgIDs)",
                RegistryHive.LocalMachine,
                new[]
                {
                    @"SOFTWARE\Classes\MSEdgeHTM",
                    @"SOFTWARE\Classes\MSEdgeMHT",
                    @"SOFTWARE\Classes\MSEdgePDF",
                }
            ),

            // ---- Shared updater registry (kept if WebView2 is present) ----
            UpdaterKeyRule(
                "Registry",
                "EdgeUpdate registry key",
                RegistryHive.LocalMachine,
                new[] { @"SOFTWARE\Microsoft\EdgeUpdate", @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate" }
            ),

            // ---- Scheduled tasks ----
            new()
            {
                Category = "Task",
                Title = "Edge browser-replacement task",
                Evaluate = () => TaskEvaluate("MicrosoftEdgeUpdateBrowserReplacement*"),
                Remediate = () => TaskRemove("MicrosoftEdgeUpdateBrowserReplacement*"),
            },
            new()
            {
                Category = "Task",
                Title = "Edge updater tasks",
                Evaluate = () =>
                    _webView2Present
                        ? KeptForWebView(TaskList("MicrosoftEdgeUpdateTask*"))
                        : TaskEvaluate("MicrosoftEdgeUpdateTask*"),
                Remediate = () => TaskRemove("MicrosoftEdgeUpdateTask*"),
            },

            // ---- Services ----
            new()
            {
                Category = "Service",
                Title = "Edge elevation service",
                Evaluate = () => ServiceEvaluate("MicrosoftEdgeElevationService"),
                Remediate = () => ServiceRemove("MicrosoftEdgeElevationService"),
            },
            new()
            {
                Category = "Service",
                Title = "EdgeUpdate services",
                Evaluate = () =>
                    _webView2Present
                        ? KeptForWebView(ServiceList("edgeupdate", "edgeupdatem"))
                        : ServiceEvaluate("edgeupdate", "edgeupdatem"),
                Remediate = () => ServiceRemove("edgeupdate", "edgeupdatem"),
            },

            // ---- Appx package ----
            new()
            {
                Category = "App",
                Title = "Edge Appx package (all users)",
                Evaluate = () =>
                {
                    var found = RunPowerShell(
                        "@(Get-AppxPackage -AllUsers -Name 'Microsoft.MicrosoftEdge','Microsoft.MicrosoftEdge.Stable' "
                            + "-ErrorAction SilentlyContinue | ForEach-Object Name | Select-Object -Unique) -join ', '"
                    );
                    return string.IsNullOrWhiteSpace(found)
                        ? (CheckState.Compliant, "Not installed")
                        : (CheckState.NeedsChange, found);
                },
                Remediate = () =>
                    RunPowerShell(
                        "Get-AppxPackage -AllUsers -Name 'Microsoft.MicrosoftEdge','Microsoft.MicrosoftEdge.Stable' "
                            + "-ErrorAction SilentlyContinue | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue",
                        180000
                    ),
            },

            // ---- Shortcuts ----
            FileRule(
                "Shortcut",
                "Edge Start-menu / desktop shortcuts",
                new[]
                {
                    Path.Combine(StartMenuPrograms, "Microsoft Edge.lnk"),
                    Path.Combine(PublicDesktop, "Microsoft Edge.lnk"),
                    Path.Combine(UserDesktop, "Microsoft Edge.lnk"),
                }
            ),
        };
    }

    // ---- Rule factories ----

    private static Rule DirRule(string category, string title, string path) =>
        new()
        {
            Category = category,
            Title = title,
            Evaluate = () =>
                Directory.Exists(path)
                    ? (CheckState.NeedsChange, path)
                    : (CheckState.Compliant, "Not present"),
            Remediate = () => Directory.Delete(path, true),
        };

    private Rule UpdaterDirRule(string category, string title, string path) =>
        new()
        {
            Category = category,
            Title = title,
            Evaluate = () =>
            {
                if (!Directory.Exists(path))
                    return (CheckState.Compliant, "Not present");
                return _webView2Present
                    ? (CheckState.Compliant, "Kept — WebView2 relies on the updater")
                    : (CheckState.NeedsChange, path);
            },
            Remediate = () => Directory.Delete(path, true),
        };

    private static Rule FileRule(string category, string title, string[] paths) =>
        new()
        {
            Category = category,
            Title = title,
            Evaluate = () =>
            {
                var present = new List<string>();
                foreach (var p in paths)
                    if (File.Exists(p))
                        present.Add(Path.GetFileName(p));
                return present.Count == 0
                    ? (CheckState.Compliant, "None found")
                    : (CheckState.NeedsChange, $"{present.Count} found");
            },
            Remediate = () =>
            {
                foreach (var p in paths)
                    if (File.Exists(p))
                        File.Delete(p);
            },
        };

    private static Rule KeyRule(string category, string title, RegistryHive hive, string[] subs) =>
        new()
        {
            Category = category,
            Title = title,
            Evaluate = () =>
                AnyKeyExists(hive, subs)
                    ? (CheckState.NeedsChange, "Present")
                    : (CheckState.Compliant, "Not present"),
            Remediate = () => DeleteKeys(hive, subs),
        };

    private Rule UpdaterKeyRule(string category, string title, RegistryHive hive, string[] subs) =>
        new()
        {
            Category = category,
            Title = title,
            Evaluate = () =>
            {
                if (!AnyKeyExists(hive, subs))
                    return (CheckState.Compliant, "Not present");
                return _webView2Present
                    ? (CheckState.Compliant, "Kept — WebView2 relies on the updater")
                    : (CheckState.NeedsChange, "Present");
            },
            Remediate = () => DeleteKeys(hive, subs),
        };

    // ---- Scheduled task / service helpers ----

    private static (CheckState, string) TaskEvaluate(string pattern)
    {
        var found = TaskList(pattern);
        return string.IsNullOrWhiteSpace(found)
            ? (CheckState.Compliant, "None registered")
            : (CheckState.NeedsChange, found);
    }

    private static string TaskList(string pattern) =>
        RunPowerShell(
            $"@(Get-ScheduledTask -TaskName '{pattern}' -ErrorAction SilentlyContinue | "
                + "ForEach-Object TaskName | Select-Object -Unique) -join ', '"
        );

    private static void TaskRemove(string pattern) =>
        RunPowerShell(
            $"Get-ScheduledTask -TaskName '{pattern}' -ErrorAction SilentlyContinue | "
                + "Unregister-ScheduledTask -Confirm:$false"
        );

    private static (CheckState, string) ServiceEvaluate(params string[] names)
    {
        var found = ServiceList(names);
        return string.IsNullOrWhiteSpace(found)
            ? (CheckState.Compliant, "Not present")
            : (CheckState.NeedsChange, found);
    }

    private static string ServiceList(params string[] names)
    {
        var quoted = string.Join(",", Array.ConvertAll(names, n => $"'{n}'"));
        return RunPowerShell(
            $"@(Get-Service -Name {quoted} -ErrorAction SilentlyContinue | "
                + "ForEach-Object Name | Select-Object -Unique) -join ', '"
        );
    }

    private static void ServiceRemove(params string[] names)
    {
        var quoted = string.Join(",", Array.ConvertAll(names, n => $"'{n}'"));
        RunPowerShell(
            $"foreach ($n in @({quoted})) {{ "
                + "Stop-Service -Name $n -Force -ErrorAction SilentlyContinue; "
                + "& sc.exe delete $n | Out-Null }",
            120000
        );
    }

    private static (CheckState, string) KeptForWebView(string found) =>
        string.IsNullOrWhiteSpace(found)
            ? (CheckState.Compliant, "Not present")
            : (CheckState.Compliant, "Kept — WebView2 relies on the updater");

    // ---- Registry / detection primitives ----

    private static bool AnyKeyExists(RegistryHive hive, string[] subs)
    {
        foreach (var sub in subs)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(sub, false);
            if (key != null)
                return true;
        }
        return false;
    }

    private static void DeleteKeys(RegistryHive hive, string[] subs)
    {
        foreach (var sub in subs)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(sub, false);
        }
    }

    private static bool IsWebView2Present()
    {
        foreach (var path in new[]
        {
            $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Guid}",
            $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}",
        })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path, false);
            if (key?.GetValue("pv") is string pv && !string.IsNullOrWhiteSpace(pv))
                return true;
        }

        return Directory.Exists(Path.Combine(ProgramFilesX86, "Microsoft", "EdgeWebView", "Application"));
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
