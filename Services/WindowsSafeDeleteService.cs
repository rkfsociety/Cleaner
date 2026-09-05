using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;

namespace Cleaner;

internal static class WindowsSafeDeleteService
{
    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileDispositionInfo = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDispositionInformation information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public static DeleteAttemptResult TryDelete(string fullPath, string root)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var directory in GetDirectoriesToLock(fullPath, root))
            {
                var directoryHandle = CreateFile(directory, GenericRead, FileShare.Read | FileShare.Write, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                if (directoryHandle.IsInvalid)
                {
                    directoryHandle.Dispose();
                    return new DeleteAttemptResult(false, ClassifyWin32Error(Marshal.GetLastWin32Error()));
                }

                if (IsReparsePoint(directoryHandle))
                {
                    directoryHandle.Dispose();
                    return new DeleteAttemptResult(false, CleanupSkipReason.ReparsePoint);
                }

                handles.Add(directoryHandle);
            }

            using var file = CreateFile(fullPath, Delete, FileShare.Read | FileShare.Write, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (file.IsInvalid)
            {
                return new DeleteAttemptResult(false, ClassifyWin32Error(Marshal.GetLastWin32Error()));
            }

            if (IsReparsePoint(file))
            {
                return new DeleteAttemptResult(false, CleanupSkipReason.ReparsePoint);
            }

            var disposition = new FileDispositionInformation { DeleteFile = true };
            return SetFileInformationByHandle(file, FileDispositionInfo, ref disposition, (uint)Marshal.SizeOf<FileDispositionInformation>())
                ? new DeleteAttemptResult(true, CleanupSkipReason.None)
                : new DeleteAttemptResult(false, ClassifyWin32Error(Marshal.GetLastWin32Error()));
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    private static bool IsReparsePoint(SafeFileHandle handle)
    {
        return !GetFileInformationByHandle(handle, out var information) || ((FileAttributes)information.FileAttributes).HasFlag(FileAttributes.ReparsePoint);
    }

    private static IEnumerable<string> GetDirectoriesToLock(string fullPath, string root)
    {
        var directories = new Stack<string>();
        var current = Path.GetDirectoryName(fullPath);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current))
        {
            directories.Push(current);
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return directories;
    }

    private static CleanupSkipReason ClassifyWin32Error(int error) => error switch
    {
        2 or 3 => CleanupSkipReason.NotFound,
        5 => CleanupSkipReason.AccessDenied,
        32 or 33 => CleanupSkipReason.FileInUse,
        _ => CleanupSkipReason.Unknown
    };
}

internal sealed record DeleteAttemptResult(bool Succeeded, CleanupSkipReason Reason);
