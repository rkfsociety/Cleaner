using System.IO;

namespace Cleaner;

public static class WindowsDriveService
{
    public static string GetSystemDriveRoot()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        return Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
    }

    public static IReadOnlyList<string> GetFixedDriveRoots()
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.RootDirectory.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return drives.Length > 0 ? drives : [GetSystemDriveRoot()];
    }
}
