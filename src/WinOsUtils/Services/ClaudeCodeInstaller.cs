using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace WinOsUtils.Services;

public sealed class ClaudeCodeInstaller
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string BinDir = Path.Combine(UserProfile, ".local", "bin");
    private static readonly string ClaudeExe = Path.Combine(BinDir, "claude.exe");
    private const string BinToken = @"%USERPROFILE%\.local\bin";

    private const string InstallScript = "irm https://claude.ai/install.ps1 | iex";

    private static readonly string PathScript =
        "$bin = Join-Path $env:USERPROFILE '.local\\bin'; "
        + "$p = [Environment]::GetEnvironmentVariable('Path','User'); "
        + "if (-not $p) { $p = '' }; "
        + "if (($p -split ';') -notcontains $bin) { "
        + "$np = if ($p.Trim().Length) { $p.TrimEnd(';') + ';' + $bin } else { $bin }; "
        + "[Environment]::SetEnvironmentVariable('Path', $np, 'User') }";

    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private readonly List<Rule> _rules;

    public ClaudeCodeInstaller()
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
                Category = "CLI",
                Title = "Claude Code CLI installed (latest)",
                Evaluate = () =>
                {
                    var version = TryGetVersion();
                    return version is not null
                        ? (CheckState.Compliant, version)
                        : (CheckState.NeedsChange, "Not installed");
                },
                Remediate = () => RunPowerShell(InstallScript, 600000),
            },
            new()
            {
                Category = "PATH",
                Title = @"~\.local\bin on your user PATH",
                Evaluate = () =>
                    UserPathHasBin()
                        ? (CheckState.Compliant, "claude runs from any new terminal")
                        : (CheckState.NeedsChange, "Folder not on PATH"),
                Remediate = () => RunPowerShell(PathScript),
            },
            new()
            {
                Category = "Shell",
                Title = "Git for Windows (Bash tool)",
                Evaluate = () =>
                    FindGitBash() is { } bash
                        ? (CheckState.Compliant, bash)
                        : (
                            CheckState.NotApplicable,
                            "Optional — install Git for Windows to enable the Bash tool; PowerShell is used otherwise"
                        ),
            },
        };
    }

    private static string? TryGetVersion()
    {
        if (File.Exists(ClaudeExe))
        {
            var v = FirstLine(RunProcess(ClaudeExe, "--version").StdOut);
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }

        var onPath = RunProcess("cmd.exe", "/c claude --version");
        if (onPath.ExitCode != 0)
            return null;

        return FirstLine(onPath.StdOut) is { Length: > 0 } line ? line : null;
    }

    private static bool UserPathHasBin()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", false);
        var raw = key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

        return raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(seg =>
                seg.Equals(BinDir, StringComparison.OrdinalIgnoreCase)
                || seg.Equals(BinToken, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static string? FindGitBash()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var candidates = new[]
        {
            Path.Combine(programFiles, "Git", "bin", "bash.exe"),
            Path.Combine(programFiles, "Git", "usr", "bin", "bash.exe"),
        };

        foreach (var c in candidates)
            if (File.Exists(c))
                return c;

        var where = RunProcess("cmd.exe", "/c where git");
        var found = FirstLine(where.StdOut);
        return where.ExitCode == 0 && !string.IsNullOrWhiteSpace(found) ? "Installed" : null;
    }

    private static string? FirstLine(string text)
    {
        var line = text
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        return line;
    }

    private static (int ExitCode, string StdOut) RunProcess(string fileName, string arguments, int waitMs = 30000)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(info);
            if (p == null)
                return (-1, "");

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(waitMs))
                return (-1, "");

            outTask.Wait(2000);
            errTask.Wait(2000);
            return (p.ExitCode, outTask.Result);
        }
        catch
        {
            return (-1, "");
        }
    }

    private static void RunPowerShell(string script, int waitMs = 60000)
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
            return;

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(waitMs);
        outTask.Wait(2000);
        errTask.Wait(2000);
    }
}
