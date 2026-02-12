using System.Runtime.InteropServices;

namespace GitDiff.Services;

public static class WindowsCredentialManager
{
    public static string? GetPassword(string targetName)
    {
        if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out var credentialPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
            }
            return null;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private const int CRED_TYPE_GENERIC = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
