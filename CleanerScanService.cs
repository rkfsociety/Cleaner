using System.IO;

namespace Cleaner;

public sealed record ScanFile(string Path, long Bytes);
public sealed record CleanupResult(int DeletedFiles, int SkippedFiles, long ReclaimedBytes);

public sealed record ScanResult(IReadOnlyList<ScanFile> UserTempFiles, IReadOnlyList<ScanFile> WindowsTempFiles, RecycleBinInfo RecycleBin)
{
    public long UserTempBytes => UserTempFiles.Sum(file => file.Bytes);
    public long WindowsTempBytes => WindowsTempFiles.Sum(file => file.Bytes);
    public long TotalBytes => UserTempBytes + WindowsTempBytes + RecycleBin.Bytes;
    public int TotalFiles => UserTempFiles.Count + WindowsTempFiles.Count + RecycleBin.Items;
}

public sealed class CleanerScanService
{
    public Task<ScanResult> ScanAsync(IEnumerable<string> selectedDrives, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var userTemp = ScanMany(BuildUserCleanupRoots(drives), cancellationToken);
            var windowsTemp = ScanMany(BuildWindowsTempRoots(drives), cancellationToken);
            return new ScanResult(userTemp, windowsTemp, new RecycleBinService().GetInfo(drives));
        }, cancellationToken);
    }

    public Task<CleanupResult> DeleteAsync(ScanResult result, IEnumerable<string> selectedDrives, bool deleteUserTemp, bool deleteWindowsTemp, bool deleteRecycleBin, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var deleted = 0;
            var skipped = 0;
            long reclaimed = 0;
            if (deleteUserTemp)
            {
                var stats = DeleteFiles(result.UserTempFiles, BuildUserCleanupRoots(drives), cancellationToken);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
            }

            if (deleteWindowsTemp)
            {
                var stats = DeleteFiles(result.WindowsTempFiles, BuildWindowsTempRoots(drives), cancellationToken);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
            }

            if (deleteRecycleBin && result.RecycleBin.Items > 0 && new RecycleBinService().Empty(drives))
            {
                deleted += result.RecycleBin.Items;
                reclaimed += result.RecycleBin.Bytes;
            }
            else if (deleteRecycleBin && result.RecycleBin.Items > 0)
            {
                skipped += result.RecycleBin.Items;
            }

            return new CleanupResult(deleted, skipped, reclaimed);
        }, cancellationToken);
    }

    private static IReadOnlyList<string> NormalizeDrives(IEnumerable<string> drives)
    {
        return drives
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetPathRoot(root) ?? root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildWindowsTempRoots(IEnumerable<string> drives)
    {
        var roots = new List<string>();
        var windowsRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var drive in drives)
        {
            var driveRoot = Path.GetPathRoot(drive);
            if (driveRoot is null)
            {
                continue;
            }

            var candidates = new[]
            {
                string.Equals(driveRoot, windowsRoot, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
                    : null,
                Path.Combine(driveRoot, "Temp"),
                Path.Combine(driveRoot, "Windows", "Temp")
            };

            roots.AddRange(candidates.Where(path => path is not null && Directory.Exists(path))!);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> BuildUserCleanupRoots(IEnumerable<string> drives)
    {
        var roots = new List<string>();
        if (IsOnSelectedDrive(Path.GetTempPath(), drives))
        {
            roots.Add(Path.GetTempPath());
            roots.AddRange(BuildBrowserCacheRoots());
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> BuildBrowserCacheRoots()
    {
        var roots = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var browserDataRoots = new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data")
        };

        foreach (var browserRoot in browserDataRoots)
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(browserRoot))
                {
                    roots.AddRange(new[]
                    {
                        Path.Combine(profile, "Cache", "Cache_Data"),
                        Path.Combine(profile, "Code Cache"),
                        Path.Combine(profile, "GPUCache")
                    }.Where(Directory.Exists));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        var firefoxProfiles = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(firefoxProfiles))
            {
                var cache = Path.Combine(profile, "cache2", "entries");
                if (Directory.Exists(cache))
                {
                    roots.Add(cache);
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return roots;
    }

    private static bool IsOnSelectedDrive(string path, IEnumerable<string> drives)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && drives.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    private static List<ScanFile> ScanMany(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var files = new List<ScanFile>();
        foreach (var path in paths)
        {
            files.AddRange(ScanDirectory(path, cancellationToken));
        }

        return files;
    }

    private static List<ScanFile> ScanDirectory(string path, CancellationToken cancellationToken)
    {
        var files = new List<ScanFile>();
        if (!Directory.Exists(path))
        {
            return files;
        }

        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { files.Add(new ScanFile(file, new FileInfo(file).Length)); }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if (!new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push(directory);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return files;
    }

    private static CleanupResult DeleteFiles(IEnumerable<ScanFile> files, IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var fullRoots = roots
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
        var deleted = 0;
        var skipped = 0;
        long reclaimed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file.Path);
                if (!fullRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) || !File.Exists(fullPath))
                {
                    skipped++;
                    continue;
                }

                File.Delete(fullPath);
                deleted++;
                reclaimed += file.Bytes;
            }
            catch (UnauthorizedAccessException) { skipped++; }
            catch (IOException) { skipped++; }
        }

        return new CleanupResult(deleted, skipped, reclaimed);
    }
}
