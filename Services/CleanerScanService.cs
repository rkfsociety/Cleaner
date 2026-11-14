using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;

namespace Cleaner;

public sealed record ScanFile(string Path, long Bytes, string Category = "", DateTime? LastWriteTimeUtc = null);
internal sealed record ScanRoot(string Path, string Category);
public sealed record CleanupResult(int DeletedFiles, int SkippedFiles, long ReclaimedBytes, IReadOnlyDictionary<CleanupSkipReason, int>? SkipReasons = null);
public sealed record ScanProgress(string Stage, int FilesFound, long BytesFound);
public sealed record CleanupProgress(string Stage, int DeletedFiles, int SkippedFiles, long ReclaimedBytes);

public sealed record ScanResult(
    IReadOnlyList<ScanFile> UserTempFiles,
    IReadOnlyList<ScanFile> WindowsTempFiles,
    RecycleBinInfo RecycleBin,
    DateTimeOffset ScannedAt,
    IReadOnlyList<ScanFile>? DevCacheFiles = null)
{
    public IReadOnlyList<ScanFile> DevCacheFilesOrEmpty => DevCacheFiles ?? [];
    public long UserTempBytes => UserTempFiles.Sum(file => file.Bytes);
    public long WindowsTempBytes => WindowsTempFiles.Sum(file => file.Bytes);
    public long DevCacheBytes => DevCacheFilesOrEmpty.Sum(file => file.Bytes);
    public long TotalBytes => UserTempBytes + WindowsTempBytes + DevCacheBytes + RecycleBin.Bytes;
    public int TotalFiles => UserTempFiles.Count + WindowsTempFiles.Count + DevCacheFilesOrEmpty.Count + RecycleBin.Items;

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

    public Task<ScanResult> ScanAsync(
        IEnumerable<string> selectedDrives,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null,
        IReadOnlyList<string>? permanentExclusions = null)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var userTemp = ScanMany(BuildUserCleanupCategorizedRoots(drives), "Временные файлы и кэш пользователя", cancellationToken, progress);
            var thumbCaches = ScanExplorerThumbCache(drives, cancellationToken);
            if (thumbCaches.Count > 0)
            {
                userTemp.AddRange(thumbCaches);
            }

            var windowsTemp = ScanMany(BuildWindowsTempCategorizedRoots(drives), "Системные временные файлы", cancellationToken, progress);
            var devCaches = ScanMany(BuildDevCacheCategorizedRoots(drives), "Кэш инструментов разработки", cancellationToken, progress);

            if (permanentExclusions is { Count: > 0 })
            {
                userTemp = userTemp.Where(file => !ExclusionListService.IsExcluded(file.Path, permanentExclusions)).ToList();
                windowsTemp = windowsTemp.Where(file => !ExclusionListService.IsExcluded(file.Path, permanentExclusions)).ToList();
                devCaches = devCaches.Where(file => !ExclusionListService.IsExcluded(file.Path, permanentExclusions)).ToList();
            }

            var totalCount = userTemp.Count + windowsTemp.Count + devCaches.Count;
            var totalBytes = userTemp.Sum(file => file.Bytes) + windowsTemp.Sum(file => file.Bytes) + devCaches.Sum(file => file.Bytes);
            progress?.Report(new ScanProgress("Корзина", totalCount, totalBytes));
            return new ScanResult(userTemp, windowsTemp, GetRecycleBinInfo(drives), DateTimeOffset.Now, devCaches);
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
        IReadOnlySet<string>? excludedPaths = null,
        bool deleteDevCaches = false,
        IReadOnlyList<string>? permanentExclusions = null)
    {
        return Task.Run(() =>
        {
            var drives = NormalizeDrives(selectedDrives);
            var deleted = 0;
            var skipped = 0;
            long reclaimed = 0;
            var skipReasons = new Dictionary<CleanupSkipReason, int>();

            bool IsFileExcluded(ScanFile file) =>
                (excludedPaths is { Count: > 0 } && excludedPaths.Contains(file.Path)) ||
                (permanentExclusions is { Count: > 0 } && ExclusionListService.IsExcluded(file.Path, permanentExclusions));

            if (deleteUserTemp)
            {
                var userFiles = result.UserTempFiles.Where(file => !IsFileExcluded(file)).ToArray();
                var userRoots = BuildUserCleanupRoots(drives).ToList();
                var explorerDir = GetExplorerThumbCacheDirectory();
                if (IsOnSelectedDrive(explorerDir, drives) && Directory.Exists(explorerDir))
                {
                    userRoots.Add(explorerDir);
                }

                var stats = DeleteFiles(userFiles, userRoots, minimumAgeHours, cancellationToken, progress, "Временные файлы и кэш пользователя", deleted, skipped, reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
                MergeSkipReasons(skipReasons, stats.SkipReasons);
            }

            if (deleteWindowsTemp)
            {
                var systemFiles = result.WindowsTempFiles.Where(file => !IsFileExcluded(file)).ToArray();
                var stats = DeleteFiles(systemFiles, BuildWindowsTempRoots(drives), minimumAgeHours, cancellationToken, progress, "Системные временные файлы", deleted, skipped, reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
                MergeSkipReasons(skipReasons, stats.SkipReasons);
            }

            if (deleteDevCaches && result.DevCacheFilesOrEmpty.Count > 0)
            {
                var devFiles = result.DevCacheFilesOrEmpty.Where(file => !IsFileExcluded(file)).ToArray();
                var stats = DeleteFiles(devFiles, BuildDevCacheRoots(drives), minimumAgeHours, cancellationToken, progress, "Кэш инструментов разработки", deleted, skipped, reclaimed);
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
                reclaimed += stats.ReclaimedBytes;
                MergeSkipReasons(skipReasons, stats.SkipReasons);
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
                        if (info.Items > 0)
                        {
                            skipReasons[CleanupSkipReason.Unknown] = skipReasons.GetValueOrDefault(CleanupSkipReason.Unknown) + info.Items;
                        }
                    }

                    progress?.Report(new CleanupProgress("Корзина", deleted, skipped, reclaimed));
                }
            }

            return new CleanupResult(deleted, skipped, reclaimed, skipReasons);
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

    internal static IReadOnlyList<string> BuildDevCacheRoots(IEnumerable<string> drives) =>
        BuildDevCacheCategorizedRoots(drives).Select(root => root.Path).ToArray();

    internal static IReadOnlyList<ScanRoot> BuildDevCacheCategorizedRoots(IEnumerable<string> drives)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<ScanRoot>
        {
            new(Path.Combine(localAppData, "NuGet", "v3-cache"), "Кэш NuGet"),
            new(Path.Combine(localAppData, "NuGet", "plugins-cache"), "Кэш NuGet"),
            new(Path.Combine(userProfile, ".nuget", "packages"), "Кэш NuGet"),
            new(Path.Combine(localAppData, "npm-cache"), "Кэш npm"),
            new(Path.Combine(appData, "npm-cache"), "Кэш npm"),
            new(Path.Combine(localAppData, "Yarn", "Cache"), "Кэш Yarn"),
            new(Path.Combine(localAppData, "pnpm", "store"), "Кэш pnpm"),
            new(Path.Combine(localAppData, "pnpm-cache"), "Кэш pnpm"),
            new(Path.Combine(localAppData, "pip", "cache"), "Кэш pip")
        };

        return candidates
            .Where(root => IsOnSelectedDrive(root.Path, drives) && Directory.Exists(root.Path))
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string GetExplorerThumbCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
    }

    internal static List<ScanFile> ScanExplorerThumbCache(IEnumerable<string> drives, CancellationToken cancellationToken)
    {
        var explorerDir = GetExplorerThumbCacheDirectory();
        if (!IsOnSelectedDrive(explorerDir, drives) || !Directory.Exists(explorerDir))
        {
            return [];
        }

        var files = new List<ScanFile>();
        try
        {
            if (File.GetAttributes(explorerDir).HasFlag(FileAttributes.ReparsePoint))
            {
                return files;
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var enumerable = new FileSystemEnumerable<ScanFile>(
                explorerDir,
                (ref FileSystemEntry entry) => new ScanFile(entry.ToFullPath(), entry.Length, "Кэш миниатюр Проводника", entry.LastWriteTimeUtc.UtcDateTime),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory &&
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    IsThumbcacheFileName(entry.FileName)
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

    internal static bool IsThumbcacheFileName(ReadOnlySpan<char> fileName) =>
        fileName.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

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
        var explorerRoot = Path.GetFullPath(GetExplorerThumbCacheDirectory()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var deleted = 0;
        var skipped = 0;
        long reclaimed = 0;
        var skipReasons = new Dictionary<CleanupSkipReason, int>();
        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime? ageThresholdUtc = minimumAgeHours > 0 ? DateTime.UtcNow.AddHours(-minimumAgeHours) : null;

        void RecordSkip(CleanupSkipReason reason)
        {
            skipped++;
            skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file.Path);
                var deletionRoot = fullRoots.FirstOrDefault(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (deletionRoot is null)
                {
                    RecordSkip(CleanupSkipReason.AccessDenied);
                    continue;
                }

                if (string.Equals(deletionRoot, explorerRoot, StringComparison.OrdinalIgnoreCase) &&
                    !IsThumbcacheFileName(Path.GetFileName(fullPath)))
                {
                    RecordSkip(CleanupSkipReason.AccessDenied);
                    continue;
                }

                if (ageThresholdUtc is { } threshold && file.LastWriteTimeUtc is { } cachedLastWriteUtc && cachedLastWriteUtc > threshold)
                {
                    RecordSkip(CleanupSkipReason.TooNew);
                    continue;
                }

                var attempt = WindowsSafeDeleteService.TryDelete(fullPath, deletionRoot, ageThresholdUtc);
                if (!attempt.Succeeded)
                {
                    var reason = attempt.Reason == CleanupSkipReason.Changed && ageThresholdUtc is not null
                        ? CleanupSkipReason.TooNew
                        : attempt.Reason;
                    RecordSkip(reason);
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
            catch (UnauthorizedAccessException) { RecordSkip(CleanupSkipReason.AccessDenied); }
            catch (IOException ex)
            {
                var win32 = ex.HResult & 0xFFFF;
                RecordSkip(win32 is 32 or 33 ? CleanupSkipReason.FileInUse : CleanupSkipReason.Unknown);
            }

            if ((deleted + skipped) % 32 == 0)
            {
                progress?.Report(new CleanupProgress(stage, deletedBefore + deleted, skippedBefore + skipped, reclaimedBefore + reclaimed));
            }
        }

        RemoveEmptySubdirectories(touchedDirectories, fullRoots);
        progress?.Report(new CleanupProgress(stage, deletedBefore + deleted, skippedBefore + skipped, reclaimedBefore + reclaimed));
        return new CleanupResult(deleted, skipped, reclaimed, skipReasons);
    }

    internal static void MergeSkipReasons(Dictionary<CleanupSkipReason, int> target, IReadOnlyDictionary<CleanupSkipReason, int>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (reason, count) in source)
        {
            if (count > 0 && reason != CleanupSkipReason.None)
            {
                target[reason] = target.GetValueOrDefault(reason) + count;
            }
        }
    }

    public static string FormatSkipReasons(IReadOnlyDictionary<CleanupSkipReason, int>? skipReasons, int minimumAgeHours = 24)
    {
        if (skipReasons is null || skipReasons.Count == 0)
        {
            return string.Empty;
        }

        var parts = skipReasons
            .Where(pair => pair.Value > 0 && pair.Key != CleanupSkipReason.None)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{FormatSkipReasonLabel(pair.Key, minimumAgeHours)}: {pair.Value:N0}")
            .ToArray();

        return parts.Length > 0 ? string.Join(", ", parts) : string.Empty;
    }

    private static string FormatSkipReasonLabel(CleanupSkipReason reason, int minimumAgeHours) => reason switch
    {
        CleanupSkipReason.TooNew => minimumAgeHours > 0 ? $"младше {minimumAgeHours} ч" : "слишком свежие",
        CleanupSkipReason.FileInUse => "занято процессами",
        CleanupSkipReason.AccessDenied => "нет прав",
        CleanupSkipReason.ReparsePoint => "ссылки/junction",
        CleanupSkipReason.NotFound => "уже удалены",
        CleanupSkipReason.Changed => "изменены после проверки",
        _ => "прочие"
    };

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
