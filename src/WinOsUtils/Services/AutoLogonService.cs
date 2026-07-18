using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinOsUtils.Services;

public sealed record AutoLogonStatus(bool Enabled, string UserName, string Domain);

/// <summary>
/// Configures Windows automatic sign-in the secure way: registry flags in the Winlogon key plus
/// the account password stored as an encrypted LSA secret ("DefaultPassword"), never as plaintext.
/// This mirrors what Sysinternals Autologon and netplwiz do. Requires Administrator elevation.
/// </summary>
public static class AutoLogonService
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string DefaultPasswordSecret = "DefaultPassword";

    public static AutoLogonStatus GetStatus()
    {
        using var key =
            Registry.LocalMachine.OpenSubKey(WinlogonKey, writable: false)
            ?? throw new InvalidOperationException("The Winlogon registry key was not found.");

        var enabled = (key.GetValue("AutoAdminLogon") as string) == "1";
        var user = key.GetValue("DefaultUserName") as string ?? string.Empty;
        var domain = key.GetValue("DefaultDomainName") as string ?? string.Empty;

        return new AutoLogonStatus(enabled, user, domain);
    }

    public static void Enable(string userName, string domain, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("A user name is required.", nameof(userName));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A password is required.", nameof(password));

        using (
            var key =
                Registry.LocalMachine.OpenSubKey(WinlogonKey, writable: true)
                ?? throw new InvalidOperationException("The Winlogon registry key was not found.")
        )
        {
            key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
            key.SetValue("DefaultUserName", userName, RegistryValueKind.String);
            key.SetValue("DefaultDomainName", domain ?? string.Empty, RegistryValueKind.String);

            // Never leave a plaintext password behind — the secret goes to LSA below.
            if (key.GetValue("DefaultPassword") is not null)
                key.DeleteValue("DefaultPassword", throwOnMissingValue: false);
        }

        StorePrivateData(DefaultPasswordSecret, password);
    }

    public static void Disable()
    {
        using (
            var key =
                Registry.LocalMachine.OpenSubKey(WinlogonKey, writable: true)
                ?? throw new InvalidOperationException("The Winlogon registry key was not found.")
        )
        {
            key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);

            if (key.GetValue("DefaultPassword") is not null)
                key.DeleteValue("DefaultPassword", throwOnMissingValue: false);
        }

        // Passing null private data deletes the LSA secret.
        StorePrivateData(DefaultPasswordSecret, null);
    }

    private static void StorePrivateData(string keyName, string? data)
    {
        var handle = OpenPolicy();
        var key = InitLsaString(keyName);
        var dataPtr = IntPtr.Zero;
        var dataString = default(NativeMethods.LSA_UNICODE_STRING);

        try
        {
            if (data is not null)
            {
                dataString = InitLsaString(data);
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.LSA_UNICODE_STRING>());
                Marshal.StructureToPtr(dataString, dataPtr, fDeleteOld: false);
            }

            var status = NativeMethods.LsaStorePrivateData(handle, ref key, dataPtr);
            ThrowIfError(status);
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPtr);
            if (dataString.Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(dataString.Buffer);
            if (key.Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(key.Buffer);

            NativeMethods.LsaClose(handle);
        }
    }

    private static IntPtr OpenPolicy()
    {
        var attributes = default(NativeMethods.LSA_OBJECT_ATTRIBUTES);
        var status = NativeMethods.LsaOpenPolicy(
            IntPtr.Zero,
            ref attributes,
            NativeMethods.POLICY_CREATE_SECRET | NativeMethods.POLICY_GET_PRIVATE_INFORMATION,
            out var handle
        );

        ThrowIfError(status);
        return handle;
    }

    private static NativeMethods.LSA_UNICODE_STRING InitLsaString(string value)
    {
        return new NativeMethods.LSA_UNICODE_STRING
        {
            Buffer = Marshal.StringToHGlobalUni(value),
            Length = (ushort)(value.Length * sizeof(char)),
            MaximumLength = (ushort)((value.Length + 1) * sizeof(char)),
        };
    }

    private static void ThrowIfError(uint ntStatus)
    {
        if (ntStatus == 0)
            return;

        var win32Error = NativeMethods.LsaNtStatusToWinError(ntStatus);
        throw new Win32Exception(win32Error);
    }
}
