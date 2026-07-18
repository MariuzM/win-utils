using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WinOsUtils.Services;

public sealed class TelemetryRemover
{
    private sealed class Rule
    {
        public string Category = "";
        public string Title = "";
        public Func<(CheckState State, string Detail)> Evaluate = () => (CheckState.NotApplicable, "");
        public Action Remediate = () => { };
    }

    private const string DataCollection = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string CloudContent = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    private const string System = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Privacy = @"Software\Microsoft\Windows\CurrentVersion\Privacy";

    private readonly List<Rule> _rules;

    public TelemetryRemover()
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

    private List<Rule> BuildRules()
    {
        return new List<Rule>
        {
            Dword("Diagnostics", "Diagnostic data set to off", RegistryHive.LocalMachine, DataCollection, "AllowTelemetry", 0),
            Dword("Diagnostics", "Feedback notifications hidden", RegistryHive.LocalMachine, DataCollection, "DoNotShowFeedbackNotifications", 1),
            Dword("Diagnostics", "Feedback frequency set to never", RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
            Dword("Diagnostics", "Tailored experiences off", RegistryHive.CurrentUser, Privacy, "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            Dword("Diagnostics", "Customer Experience Program off", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
            Dword("Diagnostics", "Windows Error Reporting off", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),

            Dword("Advertising", "Advertising ID disabled (machine)", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1),
            Dword("Advertising", "Advertising ID disabled (user)", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),

            Dword("Activity", "Activity feed disabled", RegistryHive.LocalMachine, System, "EnableActivityFeed", 0),
            Dword("Activity", "Activity history not published", RegistryHive.LocalMachine, System, "PublishUserActivities", 0),
            Dword("Activity", "Activity history not uploaded", RegistryHive.LocalMachine, System, "UploadUserActivities", 0),

            Dword("Location", "Location access turned off", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1),

            Dword("Suggestions", "Consumer features / promoted apps off", RegistryHive.LocalMachine, CloudContent, "DisableWindowsConsumerFeatures", 1),
            Dword("Suggestions", "Windows tips off", RegistryHive.LocalMachine, CloudContent, "DisableSoftLanding", 1),
            Dword("Suggestions", "Silent app installs off", RegistryHive.CurrentUser, ContentDelivery, "SilentInstalledAppsEnabled", 0),
            Dword("Suggestions", "Settings suggestions off", RegistryHive.CurrentUser, ContentDelivery, "SystemPaneSuggestionsEnabled", 0),
            Dword("Suggestions", "Start menu suggestions off", RegistryHive.CurrentUser, ContentDelivery, "SubscribedContent-338388Enabled", 0),
            Dword("Suggestions", "Lock screen tips off", RegistryHive.CurrentUser, ContentDelivery, "SubscribedContent-338387Enabled", 0),

            Dword("Typing", "Inking & typing data collection off", RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
            Dword("Typing", "Ink collection off", RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
            Dword("Typing", "Personalization data off", RegistryHive.CurrentUser, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
            Dword("Typing", "Linguistic data collection off", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\TextInput", "AllowLinguisticDataCollection", 0),

            Dword("Cortana", "Cortana disabled", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0),

            ServiceRule("Services", "Telemetry service (DiagTrack) disabled", "DiagTrack"),
            ServiceRule("Services", "Telemetry push service (dmwappushservice) disabled", "dmwappushservice"),
        };
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

    private static Rule ServiceRule(string category, string title, string service)
    {
        return new Rule
        {
            Category = category,
            Title = title,
            Evaluate = () =>
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}", false);
                if (key == null)
                    return (CheckState.NotApplicable, "Not installed");
                return key.GetValue("Start") as int? == 4
                    ? (CheckState.Compliant, "Disabled")
                    : (CheckState.NeedsChange, "Enabled");
            },
            Remediate = () =>
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}", true);
                key?.SetValue("Start", 4, RegistryValueKind.DWord);
            },
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
