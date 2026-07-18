using Microsoft.Win32;

namespace WinOsUtils.Services;

public static class SearchService
{
    private const string WinSearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    private const string SearchSettings = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";
    private const string SearchUser = @"Software\Microsoft\Windows\CurrentVersion\Search";

    public static bool IsWebEnabled()
    {
        return GetDword(RegistryHive.LocalMachine, WinSearchPolicy, "DisableWebSearch") != 1;
    }

    public static void SetWebEnabled(bool enabled)
    {
        if (enabled)
        {
            SetDword(RegistryHive.LocalMachine, WinSearchPolicy, "DisableWebSearch", 0);
            SetDword(RegistryHive.LocalMachine, WinSearchPolicy, "ConnectedSearchUseWeb", 1);
            SetDword(RegistryHive.LocalMachine, WinSearchPolicy, "EnableDynamicContentInWSB", 1);
            SetDword(RegistryHive.CurrentUser, SearchSettings, "IsDynamicSearchBoxEnabled", 1);
            SetDword(RegistryHive.CurrentUser, SearchUser, "BingSearchEnabled", 1);
        }
        else
        {
            SetDword(RegistryHive.LocalMachine, WinSearchPolicy, "DisableWebSearch", 1);
            SetDword(RegistryHive.LocalMachine, WinSearchPolicy, "ConnectedSearchUseWeb", 0);
            SetDword(RegistryHive.LocalMachine, WinSearchPolicy, "EnableDynamicContentInWSB", 0);
            SetDword(RegistryHive.CurrentUser, SearchSettings, "IsDynamicSearchBoxEnabled", 0);
            SetDword(RegistryHive.CurrentUser, SearchUser, "BingSearchEnabled", 0);
        }
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
