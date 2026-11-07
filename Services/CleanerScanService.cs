using System.IO;
using System.IO.Enumeration;

namespace Cleaner;

public sealed record ScanFile(string Path, long Bytes, string Category = "");
public sealed record CleanupResult(int DeletedFiles, int SkippedFiles, long ReclaimedBytes);
public sealed record ScanProgress(string Stage, int FilesFound, long BytesFound);
public sealed record CleanupProgress(string Stage, int DeletedFiles, int SkippedFiles, long ReclaimedBytes);

public sealed record ScanResult(IReadOnlyList<ScanFile> UserTempFiles, IReadOnlyList<ScanFile> WindowsTempFiles, RecycleBinInfo RecycleBin, DateTimeOffset ScannedAt)
{
    public long UserTempBytes => UserTempFiles.Sum(file => file.Bytes);
    public long WindowsTempBytes => WindowsTempFiles.Sum(file => file.Bytes);
    public long TotalBytes => UserTempBytes + WindowsTempBytes + RecycleBin.Bytes;
    public int TotalFiles => UserTempFiles.Count + WindowsTempFiles.Count + RecycleBin.Items;

    public bool IsFresh(TimeSpan maximumAge, DateTimeOffset now) => now - ScannedAt <= maximumAge;
}

public sealed class CleanerScanService
{
    private readonly IRecycleBinService _recycleBinService;

    public CleanerScanService() : this(new RecycleBinService())
    {
    }

    internal CleanerScanService(IRecycleBinService recycleBinService)
    {
        _recycleBinService = recycleBinService;
    }

    public RecycleBinInfo GetRecycleBinInfo(IEnumerable<string> selectedDrives) => _recycleBinService.GetInfo(NormalizeDrives(selectedDrives));
    internal static bool HasRecycleBinChanged(RecycleBinInfo scanned, RecycleBinInfo current) => scanned != current;

    public Task<ScanResult> ScanAsync(IEnumerable<string> selectedDrives, CancellationToken cancellationToken = default, IProgress<ScanProgress>? progress = null)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var userTemp = ScanMany(BuildUserCleanupRoots(drives), "Временные файлы и кэш пользователя", cancellationToken, progress);
            var windowsTemp = ScanMany(BuildWindowsTempRoots(drives), "Системные временные файлы", cancellationToken, progress);
            progress?.Report(new ScanProgress("Корзина", userTemp.Count + windowsTemp.Count, userTemp.Sum(file => file.Bytes) + windowsTemp.Sum(file => file.Bytes)));
            return new ScanResult(userTemp, windowsTemp, GetRecycleBinInfo(drives), DateTimeOffset.Now);
        }, cancellationToken);
    }

    public Task<CleanupResult> DeleteAsync(
        ScanResult result,
        IEnumerable<string> selectedDrives,
        bool deleteUserTemp,
        bool deleteWindowsTemp,
        bool deleteRecycleBin,
        int minimumAgeHours,
        CancellationToken cancellationToken = default,
        IProgress<CleanupProgress>? progress = null,
        IReadOnlySet<string>? excludedPaths = null)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var deleted = 0;
            var skipped = 0;
            long reclaimed = 0;
            if (deleteUserTemp)
            {
                var userFiles = excludedPaths is { Count: > 0 }
                    ? result.UserTempFiles.Where(file => !excludedPaths.Contains(file.Path)).ToArray()
                    : result.UserTempFiles;
                var stats = DeleteFiles(userFiles, BuildUserCleanupRoots(drives), minimumAgeHours, cancellationToken, progress, "Временные файлы и кэш пользователя", deleted, skipped, reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
            }

            if (deleteWindowsTemp)
            {
                var systemFiles = excludedPaths is { Count: > 0 }
                    ? result.WindowsTempFiles.Where(file => !excludedPaths.Contains(file.Path)).ToArray()
                    : result.WindowsTempFiles;
                var stats = DeleteFiles(systemFiles, BuildWindowsTempRoots(drives), minimumAgeHours, cancellationToken, progress, "Системные временные файлы", deleted, skipped, reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
            }

            if (deleteRecycleBin)
            {
                var beforeEmpty = _recycleBinService.GetInfoPerRoot(drives);
                foreach (var root in drives)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var succeeded = _recycleBinService.Empty(root);
                    var info = beforeEmpty.TryGetValue(root, out var value) ? value : new RecycleBinInfo(0, 0);
                    if (succeeded)
                    {
                        deleted += info.Items;
                        reclaimed += info.Bytes;
                    }
                    else
                    {
                        skipped += info.Items;
                    }

                    progress?.Report(new CleanupProgress("Корзина", deleted, skipped, reclaimed));
                }
            }

            return new CleanupResult(deleted, skipped, reclaimed);
        }, cancellationToken);
    }

    internal static IReadOnlyList<string> NormalizeDrives(IEnumerable<string> drives)
    {
        return drives
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetPathRoot(root) ?? root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildWindowsTempRoots(IEnumerable<string> drives)
    {
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsRoot = Path.GetPathRoot(windowsFolder);
        if (windowsRoot is null || !drives.Contains(windowsRoot, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var candidates = new[]
        {
            Path.Combine(windowsFolder, "Temp"),
            Path.Combine(windowsFolder, "Minidump"),
            Path.Combine(windowsFolder, "SoftwareDistribution", "Download"),
            Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "Temp")
        };

        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<string> BuildUserCleanupRoots(IEnumerable<string> drives)
    {
        var roots = new List<string>();
        if (IsOnSelectedDrive(Path.GetTempPath(), drives))
        {
            roots.Add(Path.GetTempPath());
            roots.AddRange(BuildUserDiagnosticAndCacheRoots());
            roots.AddRange(BuildBrowserCacheRoots());
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> BuildUserDiagnosticAndCacheRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(localAppData, "CrashDumps"),
            Path.Combine(localAppData, "D3DSCache"),
            Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"),
            Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(localAppData, "Microsoft", "Windows", "WER", "Temp")
        };

        var appCacheFolders = new[]
        {
            Path.Combine(appData, "Code"),
            Path.Combine(appData, "discord"),
            Path.Combine(appData, "Slack")
        };

        foreach (var appRoot in appCacheFolders)
        {
            candidates.Add(Path.Combine(appRoot, "Cache", "Cache_Data"));
            candidates.Add(Path.Combine(appRoot, "Code Cache"));
            candidates.Add(Path.Combine(appRoot, "GPUCache"));
        }

        return candidates.Where(Directory.Exists).ToArray();
    }

    private static IReadOnlyList<string> BuildBrowserCacheRoots()
    {
        var roots = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var chromiumUserDataRoots = new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(localAppData, "Vivaldi", "User Data"),
            Path.Combine(localAppData, "Yandex", "YandexBrowser", "User Data")
        };

        foreach (var browserRoot in chromiumUserDataRoots.Where(Directory.Exists))
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

        var operaRoots = new[]
        {
            Path.Combine(localAppData, "Opera Software", "Opera Stable"),
            Path.Combine(localAppData, "Opera Software", "Opera GX Stable"),
            Path.Combine(appData, "Opera Software", "Opera Stable"),
            Path.Combine(appData, "Opera Software", "Opera GX Stable")
        };

        foreach (var operaRoot in operaRoots.Where(Directory.Exists))
        {
            roots.AddRange(new[]
            {
                Path.Combine(operaRoot, "Cache", "Cache_Data"),
                Path.Combine(operaRoot, "Code Cache"),
                Path.Combine(operaRoot, "GPUCache")
            }.Where(Directory.Exists));
        }

        var firefoxProfiles = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
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
        }

        return roots;
    }

    private static bool IsOnSelectedDrive(string path, IEnumerable<string> drives)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && drives.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    internal static string DescribeSourceCategory(string path, string fallbackStage)
    {
        if (path.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Кэш Chrome";
        if (path.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Кэш Edge";
        if (path.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "Кэш Brave";
        if (path.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "Кэш Vivaldi";
        if (path.Contains("YandexBrowser", StringComparison.OrdinalIgnoreCase)) return "Кэш Яндекс Браузера";
        if (path.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "Кэш Opera";
        if (path.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Кэш Firefox";
        if (path.Contains("CrashDumps", StringComparison.OrdinalIgnoreCase) || path.Contains("Minidump", StringComparison.OrdinalIgnoreCase)) return "Дампы сбоев";
        if (path.Contains("\\WER\\", StringComparison.OrdinalIgnoreCase)) return "Отчёты об ошибках WER";
        if (path.Contains("SoftwareDistribution\\Download", StringComparison.OrdinalIgnoreCase)) return "Кэш обновлений Windows";
        if (path.Contains("D3DSCache", StringComparison.OrdinalIgnoreCase)) return "Кэш DirectX шейдеров";
        if (path.Contains("INetCache", StringComparison.OrdinalIgnoreCase)) return "Временный веб-кэш Windows";
        if (path.Contains("\\Code\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\discord\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\Slack\\", StringComparison.OrdinalIgnoreCase)) return "Кэш приложений";
        return fallbackStage;
    }

    private static List<ScanFile> ScanMany(IEnumerable<string> paths, string stage, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
    {
        var files = new List<ScanFile>();
        var totalBytes = 0L;
        foreach (var path in paths)
        {
            foreach (var file in ScanDirectory(path, stage, cancellationToken))
            {
                files.Add(file);
                totalBytes += file.Bytes;
                if ((files.Count & 127) == 0)
                {
                    progress?.Report(new ScanProgress(stage, files.Count, totalBytes));
                }
            }
        }

        return files;
    }

    private static readonly EnumerationOptions ScanEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static List<ScanFile> ScanDirectory(string path, string stage, CancellationToken cancellationToken)
    {
        var files = new List<ScanFile>();
        if (!Directory.Exists(path))
        {
            return files;
        }

        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return files;
            }

            var category = DescribeSourceCategory(path, stage);
            var enumerable = new FileSystemEnumerable<ScanFile>(
                path,
                (ref FileSystemEntry entry) => new ScanFile(entry.ToFullPath(), entry.Length, category),
                ScanEnumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };

            foreach (var file in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return files;
    }

    internal static CleanupResult DeleteFiles(IEnumerable<ScanFile> files, IEnumerable<string> roots, int minimumAgeHours, CancellationToken cancellationToken, IProgress<CleanupProgress>? progress = null, string stage = "Очистка", int deletedBefore = 0, int skippedBefore = 0, long reclaimedBefore = 0)
    {
        var fullRoots = roots
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
        var deleted = 0;
        var skipped = 0;
        long reclaimed = 0;
        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ageThresholdUtc = minimumAgeHours > 0 ? DateTime.UtcNow.AddHours(-minimumAgeHours) : DateTime.MaxValue;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file.Path);
                var deletionRoot = fullRoots.FirstOrDefault(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (deletionRoot is null || !File.Exists(fullPath))
                {
                    skipped++;
                    continue;
                }

                if (minimumAgeHours > 0 && File.GetLastWriteTimeUtc(fullPath) > ageThresholdUtc)
                {
                    skipped++;
                    continue;
                }

                if (!WindowsSafeDeleteService.TryDelete(fullPath, deletionRoot).Succeeded)
                {
                    skipped++;
                    continue;
                }

                deleted++;
                reclaimed += file.Bytes;
                var parentDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    touchedDirectories.Add(parentDir);
                }
            }
            catch (UnauthorizedAccessException) { skipped++; }
            catch (IOException) { skipped++; }

            if ((deleted + skipped) % 32 == 0)
            {
                progress?.Report(new CleanupProgress(stage, deletedBefore + deleted, skippedBefore + skipped, reclaimedBefore + reclaimed));
            }
        }

        RemoveEmptySubdirectories(touchedDirectories, fullRoots);
        progress?.Report(new CleanupProgress(stage, deletedBefore + deleted, skippedBefore + skipped, reclaimedBefore + reclaimed));
        return new CleanupResult(deleted, skipped, reclaimed);
    }

    internal static void RemoveEmptySubdirectories(IEnumerable<string> directories, IReadOnlyList<string> fullRoots)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            var current = directory;
            while (!string.IsNullOrEmpty(current))
            {
                var normalized = current.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullRoots.Any(root => string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)) ||
                    !fullRoots.Any(root => normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }

                candidates.Add(current);
                current = Path.GetDirectoryName(current);
            }
        }

        foreach (var candidate in candidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(candidate) &&
                    !File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint) &&
                    !Directory.EnumerateFileSystemEntries(candidate).Any())
                {
                    Directory.Delete(candidate, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool IsSafePathForDeletion(string fullPath, IEnumerable<string> fullRoots)
    {
        var root = fullRoots.FirstOrDefault(candidate => fullPath.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            return false;
        }

        try
        {
            var current = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                var normalizedCurrent = current.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (string.Equals(normalizedCurrent, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }

            return !File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
