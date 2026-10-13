using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Kairo.Core.Secrets;

namespace Kairo.Windows;

/// <summary>Secrets in Windows Credential Manager (generic credentials, per user, persisted locally).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class CredentialStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Get(string name)
    {
        if (!CredRead(name, CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == 0)
                return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public unsafe void Set(string name, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        fixed (byte* blob = bytes)
        fixed (char* target = name)
        fixed (char* user = Environment.UserName)
        {
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = (nint)target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = (nint)blob,
                Persist = CredPersistLocalMachine,
                UserName = (nint)user,
            };
            if (!CredWrite(cred, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        Array.Clear(bytes);
    }

    public void Delete(string name)
    {
        if (!CredDelete(name, CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() is var error && error != ErrorNotFound)
            throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);
}
