using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace WinOsUtils.Services;

// Reclaims Start-menu height for pinned items: switches Start to the "More pins" layout and
// hides the Recommended section (and the recent apps/docs/tips that feed it) so pins get the
// space. Every value maps to a documented Explorer / Group Policy key and is reversible from
// Settings › Personalization › Start.
public sealed class StartMenuTweaker
{
    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";
    private const string ExplorerPolicyUser = @"Software\Policies\Microsoft\Windows\Explorer";

    private readonly List<Rule> _rules;

    public StartMenuTweaker()
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
                // A failed change surfaces on the next scan.
            }
        }
    }

    private List<Rule> BuildRules()
    {
        return new List<Rule>
        {
            Dword(
                "Layout",
                "Start layout set to \"More pins\"",
                RegistryHive.CurrentUser,
                Advanced,
                "Start_Layout",
                1
            ),

            Dword(
                "Recommended",
                "Recommended section hidden (machine policy)",
                RegistryHive.LocalMachine,
                ExplorerPolicy,
                "HideRecommendedSection",
                1
            ),
            Dword(
                "Recommended",
                "Recommended section hidden (user policy)",
                RegistryHive.CurrentUser,
                ExplorerPolicyUser,
                "HideRecommendedSection",
                1
            ),
            Dword(
                "Recommended",
                "Recently added apps hidden",
                RegistryHive.CurrentUser,
                Advanced,
                "Start_TrackProgs",
                0
            ),
            Dword(
                "Recommended",
                "Recently opened items hidden",
                RegistryHive.CurrentUser,
                Advanced,
                "Start_TrackDocs",
                0
            ),
            Dword(
                "Recommended",
                "Tips & app promotions off",
                RegistryHive.CurrentUser,
                Advanced,
                "Start_IrisRecommendations",
                0
            ),
            Dword(
                "Recommended",
                "Recommended websites hidden",
                RegistryHive.LocalMachine,
                ExplorerPolicy,
                "HideRecommendedPersonalizedSites",
                1
            ),

            Dword(
                "Start",
                "Account notifications off",
                RegistryHive.CurrentUser,
                Advanced,
                "Start_AccountNotifications",
                0
            ),
        };
    }

    // Start-menu layout changes only take effect once the shell is reloaded.
    public static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
                // Ignore — a process we can't kill isn't ours to restart.
            }
        }

        // Windows relaunches the shell automatically (AutoRestartShell); only start it
        // ourselves if it hasn't come back, to avoid opening a stray folder window.
        Thread.Sleep(1500);
        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true });
            }
            catch
            {
                // Nothing more we can do; a sign-out will restore the shell.
            }
        }
    }

    private static Rule Dword(string category, string title, RegistryHive hive, string path, string name, int desired)
    {
        return new Rule
        {
            Category = category,
            Title = title,
            Evaluate = () =>
                GetDword(hive, path, name) == desired
                    ? (CheckState.Compliant, $"Set ({name}={desired})")
                    : (CheckState.NeedsChange, "Not configured"),
            Remediate = () => SetDword(hive, path, name, desired),
        };
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
}
