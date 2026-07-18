using System;
using System.Runtime.InteropServices;

namespace WinUtils.Services;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    // Access rights for the LSA policy handle.
    internal const uint POLICY_CREATE_SECRET = 0x00000020;
    internal const uint POLICY_GET_PRIVATE_INFORMATION = 0x00000004;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint LsaOpenPolicy(
        IntPtr SystemName,
        ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
        uint DesiredAccess,
        out IntPtr PolicyHandle
    );

    // PrivateData is a pointer to an LSA_UNICODE_STRING, or IntPtr.Zero to delete the secret.
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint LsaStorePrivateData(
        IntPtr PolicyHandle,
        ref LSA_UNICODE_STRING KeyName,
        IntPtr PrivateData
    );

    [DllImport("advapi32.dll")]
    internal static extern uint LsaClose(IntPtr ObjectHandle);

    [DllImport("advapi32.dll")]
    internal static extern int LsaNtStatusToWinError(uint status);
}
