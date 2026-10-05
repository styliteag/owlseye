using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Owlseye.Windows;

/// <summary>The few Win32 calls owlseye needs, declared by hand.</summary>
internal static unsafe partial class Native
{
    public const uint ReadControl = 0x00020000, WriteDac = 0x00040000;
    public const uint FileShareRead = 0x1, FileShareWrite = 0x2, FileShareDelete = 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileAttributeDirectory = 0x10, FileAttributeReparsePoint = 0x400;

    public const int SeFileObject = 1;
    public const uint DaclSecurityInformation = 0x4;
    public const uint ProtectedDaclSecurityInformation = 0x80000000, UnprotectedDaclSecurityInformation = 0x20000000;

    public const int ErrorAccessDenied = 5;
    public const int ErrorAlreadyExists = 183, ErrorNoneMapped = 1332, ErrorInsufficientBuffer = 122, ErrorMoreData = 234;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint size, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectory(string path, nint security);

    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveDirectory(string path);

    [LibraryImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetNamedSecurityInfo(string name, int type, uint info, nint* owner, nint* group, nint* dacl, nint* sacl, nint* sd);

    [LibraryImport("advapi32.dll")]
    public static partial int GetSecurityInfo(SafeFileHandle handle, int type, uint info, nint* owner, nint* group, nint* dacl, nint* sacl, nint* sd);

    [LibraryImport("advapi32.dll")]
    public static partial int SetSecurityInfo(SafeFileHandle handle, int type, uint info, nint owner, nint group, byte* dacl, nint sacl);

    [LibraryImport("advapi32.dll")]
    public static partial uint GetSecurityDescriptorLength(nint sd);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupAccountSid(nint system, byte* sid, char* name, ref uint nameLen, char* domain, ref uint domainLen, out int use);

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetUniversalNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WNetGetUniversalName(string localPath, int infoLevel, byte* buffer, ref uint size);

    public const int UniversalNameInfoLevel = 1;

    public static Win32Exception LastError(string func) => new(Marshal.GetLastPInvokeError(), func);
}
