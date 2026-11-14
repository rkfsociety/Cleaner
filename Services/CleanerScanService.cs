using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;

namespace Cleaner;

public sealed record ScanFile(string Path, long Bytes, string Category = "", DateTime? LastWriteTimeUtc = null);
internal sealed record ScanRoot(string Path, string Category);
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
            var userTemp = ScanMany(BuildUserCleanupCategorizedRoots(drives), "Временные файлы и кэш пользователя", cancellationToken, progress);
            var windowsTemp = ScanMany(BuildWindowsTempCategorizedRoots(drives), "Системные временные файлы", cancellationToken, progress);
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

    internal static IReadOnlyList<string> BuildWindowsTempRoots(IEnumerable<string> drives) =>
        BuildWindowsTempCategorizedRoots(drives).Select(root => root.Path).ToArray();

    internal static IReadOnlyList<ScanRoot> BuildWindowsTempCategorizedRoots(IEnumerable<string> drives)
    {
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsRoot = Path.GetPathRoot(windowsFolder);
        if (windowsRoot is null || !drives.Contains(windowsRoot, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var candidates = new ScanRoot[]
        {
            new(Path.Combine(windowsFolder, "Temp"), "Системные временные файлы"),
            new(Path.Combine(windowsFolder, "Minidump"), "Дампы сбоев"),
            new(Path.Combine(windowsFolder, "SoftwareDistribution", "Download"), "Кэш обновлений Windows"),
            new(Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportArchive"), "Отчёты об ошибках WER"),
            new(Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportQueue"), "Отчёты об ошибках WER"),
            new(Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "Temp"), "Отчёты об ошибках WER")
        };

        return candidates
            .Where(root => Directory.Exists(root.Path))
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildUserCleanupRoots(IEnumerable<string> drives) =>
        BuildUserCleanupCategorizedRoots(drives).Select(root => root.Path).ToArray();

    internal static IReadOnlyList<ScanRoot> BuildUserCleanupCategorizedRoots(IEnumerable<string> drives)
    {
        var roots = new List<ScanRoot>();
        if (IsOnSelectedDrive(Path.GetTempPath(), drives))
        {
            roots.Add(new ScanRoot(Path.GetTempPath(), "Временные файлы и кэш пользователя"));
            roots.AddRange(BuildUserDiagnosticAndCacheRoots());
            roots.AddRange(BuildBrowserCacheRoots());
        }

        return roots.DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<ScanRoot> BuildUserDiagnosticAndCacheRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<ScanRoot>
        {
            new(Path.Combine(localAppData, "CrashDumps"), "Дампы сбоев"),
            new(Path.Combine(localAppData, "D3DSCache"), "Кэш DirectX шейдеров"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"), "Временный веб-кэш Windows"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"), "Отчёты об ошибках WER"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"), "Отчёты об ошибках WER"),
            new(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "Temp"), "Отчёты об ошибках WER")
        };

        var appCacheFolders = new[]
        {
            Path.Combine(appData, "Code"),
            Path.Combine(appData, "discord"),
            Path.Combine(appData, "Slack")
        };

        foreach (var appRoot in appCacheFolders)
        {
            candidates.Add(new ScanRoot(Path.Combine(appRoot, "Cache", "Cache_Data"), "Кэш приложений"));
            candidates.Add(new ScanRoot(Path.Combine(appRoot, "Code Cache"), "Кэш приложений"));
            candidates.Add(new ScanRoot(Path.Combine(appRoot, "GPUCache"), "Кэш приложений"));
        }

        return candidates.Where(root => Directory.Exists(root.Path)).ToArray();
    }

    private static IReadOnlyList<ScanRoot> BuildBrowserCacheRoots()
    {
        var roots = new List<ScanRoot>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var chromiumUserDataRoots = new (string Path, string Category)[]
        {
            (Path.Combine(localAppData, "Google", "Chrome", "User Data"), "Кэш Chrome"),
            (Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), "Кэш Edge"),
            (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), "Кэш Brave"),
            (Path.Combine(localAppData, "Vivaldi", "User Data"), "Кэш Vivaldi"),
            (Path.Combine(localAppData, "Yandex", "YandexBrowser", "User Data"), "Кэш Яндекс Браузера")
        };

        foreach (var (browserRoot, category) in chromiumUserDataRoots.Where(entry => Directory.Exists(entry.Path)))
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
                    }
                    .Where(Directory.Exists)
                    .Select(path => new ScanRoot(path, category)));
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
            }
            .Where(Directory.Exists)
            .Select(path => new ScanRoot(path, "Кэш Opera")));
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
                        roots.Add(new ScanRoot(cache, "Кэш Firefox"));
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

    private static List<ScanFile> ScanMany(IEnumerable<ScanRoot> roots, string stage, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
    {
        var rootList = roots as IReadOnlyList<ScanRoot> ?? roots.ToArray();
        if (rootList.Count == 0)
        {
            return [];
        }

        var buckets = new ConcurrentBag<List<ScanFile>>();
        var totalFiles = 0;
        var totalBytes = 0L;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
            CancellationToken = cancellationToken
        };

        Parallel.ForEach(rootList, options, root =>
        {
            var localFiles = ScanDirectory(root.Path, root.Category, cancellationToken, bytes =>
            {
                var filesCount = Interlocked.Increment(ref totalFiles);
                var bytesCount = Interlocked.Add(ref totalBytes, bytes);
                if ((filesCount & 127) == 0)
                {
                    progress?.Report(new ScanProgress(stage, filesCount, bytesCount));
                }
            });

            if (localFiles.Count > 0)
            {
                buckets.Add(localFiles);
            }
        });

        var files = new List<ScanFile>(totalFiles);
        foreach (var bucket in buckets)
        {
            files.AddRange(bucket);
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

    internal static List<ScanFile> ScanDirectory(string path, string category, CancellationToken cancellationToken, Action<long>? onFileFound = null)
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

            var enumerable = new FileSystemEnumerable<ScanFile>(
                path,
                (ref FileSystemEntry entry) => new ScanFile(entry.ToFullPath(), entry.Length, category, entry.LastWriteTimeUtc.UtcDateTime),
                ScanEnumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };

            foreach (var file in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
                onFileFound?.Invoke(file.Bytes);
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
        DateTime? ageThresholdUtc = minimumAgeHours > 0 ? DateTime.UtcNow.AddHours(-minimumAgeHours) : null;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file.Path);
                var deletionRoot = fullRoots.FirstOrDefault(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (deletionRoot is null)
                {
                    skipped++;
                    continue;
                }

                if (ageThresholdUtc is { } threshold && file.LastWriteTimeUtc is { } cachedLastWriteUtc && cachedLastWriteUtc > threshold)
                {
                    skipped++;
                    continue;
                }

                if (!WindowsSafeDeleteService.TryDelete(fullPath, deletionRoot, ageThresholdUtc).Succeeded)
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
