using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace WindowsChangeJournal.Services;

public sealed record FileIdentity(string Key, long? Size, bool IsDirectory);

public static class FileIdentityService
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public static FileIdentity? TryRead(string path)
    {
        try
        {
            using var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
            var isDirectory = ((FileAttributes)info.FileAttributes & FileAttributes.Directory) != 0;
            long? size = isDirectory ? null : (long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow);
            return new FileIdentity($"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}", size, isDirectory);
        }
        catch { return null; }
    }
}
