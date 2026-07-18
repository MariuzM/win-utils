using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WinOsUtils.Services;

public sealed class OneDriveRemover
{
    private const string Clsid = "{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\OneDrive";

    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private readonly string _localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private readonly List<Rule> _rules;

    public OneDriveRemover()
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
        var localOneDrive = Path.Combine(_localAppData, "Microsoft", "OneDrive", "OneDrive.exe");

        var folders = new (string Path, string Label)[]
        {
            (Path.Combine(_userProfile, "OneDrive"), "User OneDrive folder (your files)"),
            (Path.Combine(_localAppData, "Microsoft", "OneDrive"), "OneDrive app data"),
            (Path.Combine(_programData, "Microsoft OneDrive"), "OneDrive program data"),
            (@"C:\OneDriveTemp", "OneDrive temp folder"),
        };

        var rules = new List<Rule>
        {
            new()
            {
                Category = "Process",
                Title = "OneDrive is not running",
                Evaluate = () =>
                {
                    var count = Process.GetProcessesByName("OneDrive").Length;
                    return count > 0
                        ? (CheckState.NeedsChange, $"{count} OneDrive process(es) running")
                        : (CheckState.Compliant, "Not running");
                },
                Remediate = KillOneDrive,
            },
            new()
            {
                Category = "Uninstall",
                Title = "OneDrive is uninstalled",
                Evaluate = () =>
                    File.Exists(localOneDrive)
                        ? (CheckState.NeedsChange, $"Installed ({FileVersion(localOneDrive)})")
                        : (CheckState.Compliant, "Not installed for this user"),
                Remediate = () =>
                {
                    KillOneDrive();
                    var uninstaller = FindUninstaller();
                    if (uninstaller != null)
                        RunProcess(uninstaller, "/uninstall", 120000);
                },
            },
            new()
            {
                Category = "Registry",
                Title = "No auto-reinstall entry (HKLM Run)",
                Evaluate = () =>
                    RunValueExists(RegistryHive.LocalMachine, RegistryView.Registry64, "OneDriveSetup")
                    || RunValueExists(RegistryHive.LocalMachine, RegistryView.Registry32, "OneDriveSetup")
                        ? (CheckState.NeedsChange, "OneDriveSetup runs for every account")
                        : (CheckState.Compliant, "Absent"),
                Remediate = () =>
                {
                    DeleteRunValue(RegistryHive.LocalMachine, RegistryView.Registry64, "OneDriveSetup");
                    DeleteRunValue(RegistryHive.LocalMachine, RegistryView.Registry32, "OneDriveSetup");
                },
            },
            new()
            {
                Category = "Registry",
                Title = "No startup entry (HKCU Run)",
                Evaluate = () =>
                    RunValueExists(RegistryHive.CurrentUser, RegistryView.Registry64, "OneDrive")
                        ? (CheckState.NeedsChange, "OneDrive starts at sign-in")
                        : (CheckState.Compliant, "Absent"),
                Remediate = () => DeleteRunValue(RegistryHive.CurrentUser, RegistryView.Registry64, "OneDrive"),
            },
            new()
            {
                Category = "Policy",
                Title = "Sync disabled by policy",
                Evaluate = () =>
                    GetDword(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyKey, "DisableFileSyncNGSC") == 1
                        ? (CheckState.Compliant, "DisableFileSyncNGSC = 1")
                        : (CheckState.NeedsChange, "Policy not set"),
                Remediate = () =>
                    SetDword(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyKey, "DisableFileSyncNGSC", 1),
            },
            new()
            {
                Category = "Explorer",
                Title = "Hidden from File Explorer sidebar",
                Evaluate = () =>
                    ClsidPinned(RegistryView.Registry64) || ClsidPinned(RegistryView.Registry32)
                        ? (CheckState.NeedsChange, "OneDrive shows in the navigation pane")
                        : (CheckState.Compliant, "Not pinned"),
                Remediate = () =>
                {
                    ClsidHide(RegistryView.Registry64);
                    ClsidHide(RegistryView.Registry32);
                },
            },
            new()
            {
                Category = "Tasks",
                Title = "No OneDrive scheduled tasks",
                Evaluate = () =>
                {
                    var count = FindOneDriveTasks().Count;
                    return count > 0
                        ? (CheckState.NeedsChange, $"{count} scheduled task(s)")
                        : (CheckState.Compliant, "None");
                },
                Remediate = () =>
                {
                    foreach (var name in FindOneDriveTasks())
                        RunProcess("schtasks.exe", $"/Delete /TN \"{name}\" /F", 15000);
                },
            },
        };

        foreach (var folder in folders)
        {
            var path = folder.Path;
            var label = folder.Label;
            rules.Add(
                new Rule
                {
                    Category = "Files",
                    Title = label,
                    Evaluate = () =>
                    {
                        if (!Directory.Exists(path))
                            return (CheckState.Compliant, $"Removed — {path}");

                        var (files, bytes) = FolderStats(path);
                        return (CheckState.NeedsChange, $"{files} files, {FormatSize(bytes)} — {path}");
                    },
                    Remediate = () => DeleteFolder(path),
                }
            );
        }

        return rules;
    }

    private static void KillOneDrive()
    {
        foreach (var p in Process.GetProcessesByName("OneDrive"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch
            {
                // Ignore processes that exit on their own.
            }
        }
    }

    private string? FindUninstaller()
    {
        var candidates = new[]
        {
            Path.Combine(_localAppData, "Microsoft", "OneDrive", "OneDrive.exe"),
            Path.Combine(_windows, "System32", "OneDriveSetup.exe"),
            Path.Combine(_windows, "SysWOW64", "OneDriveSetup.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private List<string> FindOneDriveTasks()
    {
        var tasksDir = Path.Combine(_windows, "System32", "Tasks");
        if (!Directory.Exists(tasksDir))
            return new List<string>();

        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tasksDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var relative = file.Substring(tasksDir.Length).Replace('/', '\\').TrimStart('\\');
            names.Add("\\" + relative);
        }
        return names;
    }

    private static bool RunValueExists(RegistryHive hive, RegistryView view, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(RunKey, false);
        return key?.GetValue(name) != null;
    }

    private static void DeleteRunValue(RegistryHive hive, RegistryView view, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(RunKey, true);
        if (key?.GetValue(name) != null)
            key.DeleteValue(name, false);
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

    private static bool ClsidPinned(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        using var key = baseKey.OpenSubKey($@"CLSID\{Clsid}", false);
        if (key == null)
            return false;

        return key.GetValue("System.IsPinnedToNameSpaceTree") is int value ? value != 0 : true;
    }

    private static void ClsidHide(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        using var key = baseKey.OpenSubKey($@"CLSID\{Clsid}", true);
        key?.SetValue("System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord);
    }

    private static void RunProcess(string file, string arguments, int waitMs)
    {
        var info = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(info);
        p?.WaitForExit(waitMs);
    }

    private static string FileVersion(string exe)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "unknown version";
        }
        catch
        {
            return "unknown version";
        }
    }

    private static (int Files, long Bytes) FolderStats(string path)
    {
        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                files++;
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                    // Skip files that can't be measured.
                }
            }
        }
        catch
        {
            // Return whatever was counted before the error.
        }
        return (files, bytes);
    }

    private static void DeleteFolder(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // Best effort before delete.
            }
        }
        Directory.Delete(path, true);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
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
