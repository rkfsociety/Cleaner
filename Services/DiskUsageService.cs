using System.IO;
using System.Text.Json;

namespace Cleaner;

public sealed record DiskUsageSegment(string Name, long Bytes, string Color);
public sealed record DiskUsageDriveSummary(string Root, long TotalBytes, long FreeBytes);

public sealed record DiskUsageSnapshot(long TotalBytes, IReadOnlyList<DiskUsageSegment> Segments, int SkippedDrives = 0, IReadOnlyList<DiskUsageDriveSummary>? Drives = null)
{
    public bool FromCache { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - Segments.FirstOrDefault(segment => segment.Name == "Свободно")?.Bytes ?? 0);
}

/// <summary>Считает крупные категории на системном диске для диаграммы обзора.</summary>
public sealed class DiskUsageService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly string _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleaner", "disk-usage-cache.json");
    private static readonly (string Name, string RelativePath, string Color)[] KnownCategories =
    [
        ("Windows", "Windows", "#6C5CE7"),
        ("Программы", "Program Files", "#EF8E5B"),
        ("Пользователи", "Users", "#45B79C"),
        ("Данные программ", "ProgramData", "#4B8BCB")
    ];

    public Task<DiskUsageSnapshot?> ReadAsync(IEnumerable<string> driveRoots, CancellationToken cancellationToken = default)
    {
        var roots = driveRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.Run(() => ReadCached(roots, cancellationToken), cancellationToken);
    }

    private DiskUsageSnapshot? ReadCached(IReadOnlyList<string> driveRoots, CancellationToken cancellationToken)
    {
        var signature = TryBuildSignature(driveRoots);
        if (signature is not null && TryReadCache(signature, out var cached))
        {
            return cached with { FromCache = true };
        }

        var snapshot = Read(driveRoots, cancellationToken);
        if (snapshot is not null && signature is not null && !cancellationToken.IsCancellationRequested)
        {
            TryWriteCache(signature, snapshot);
        }

        return snapshot;
    }

    internal static DiskUsageSnapshot? Read(IEnumerable<string> driveRoots, CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        long totalFreeBytes = 0;
        var skippedDrives = 0;
        var driveSummaries = new List<DiskUsageDriveSummary>();
        var categoryBytes = KnownCategories.ToDictionary(category => category.Name, _ => 0L, StringComparer.OrdinalIgnoreCase);
        foreach (var driveRoot in driveRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady || drive.TotalSize <= 0) continue;

                var free = Math.Clamp(drive.AvailableFreeSpace, 0, drive.TotalSize);
                driveSummaries.Add(new DiskUsageDriveSummary(drive.RootDirectory.FullName, drive.TotalSize, free));
                totalBytes += drive.TotalSize;
                totalFreeBytes += free;
                long knownBytes = 0;
                foreach (var category in KnownCategories)
                {
                    var bytes = GetDirectoryBytes(Path.Combine(drive.RootDirectory.FullName, category.RelativePath), cancellationToken);
                    bytes = Math.Min(bytes, drive.TotalSize - free - knownBytes);
                    knownBytes += bytes;
                    categoryBytes[category.Name] += bytes;
                }
            }
            catch (IOException) { skippedDrives++; }
            catch (UnauthorizedAccessException) { skippedDrives++; }
            catch (ArgumentException) { skippedDrives++; }
        }

        if (totalBytes <= 0) return null;
        var segments = KnownCategories
            .Select(category => new DiskUsageSegment(category.Name, categoryBytes[category.Name], category.Color))
            .ToList();
        var knownTotal = segments.Sum(segment => segment.Bytes);
        segments.Add(new DiskUsageSegment("Прочее", Math.Max(0, totalBytes - totalFreeBytes - knownTotal), "#A9B0C2"));
        segments.Add(new DiskUsageSegment("Свободно", totalFreeBytes, "#B8B1F6"));
        return new DiskUsageSnapshot(totalBytes, segments, skippedDrives, driveSummaries);
    }

    private string? TryBuildSignature(IEnumerable<string> driveRoots)
    {
        try
        {
            var values = driveRoots.Select(root =>
            {
                var drive = new DriveInfo(root);
                const long cacheBucket = 64L * 1024 * 1024;
                var freeBucket = drive.AvailableFreeSpace / cacheBucket;
                return $"{drive.RootDirectory.FullName}|{drive.TotalSize}|{freeBucket}";
            });
            return string.Join(";", values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private bool TryReadCache(string signature, out DiskUsageSnapshot snapshot)
    {
        snapshot = default!;
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var cache = JsonSerializer.Deserialize<DiskUsageCache>(File.ReadAllText(_cachePath));
            if (cache is null || !string.Equals(cache.Signature, signature, StringComparison.Ordinal) || DateTimeOffset.UtcNow - cache.CapturedAt > CacheLifetime)
            {
                return false;
            }

            snapshot = cache.Snapshot;
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void TryWriteCache(string signature, DiskUsageSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var cache = new DiskUsageCache(signature, DateTimeOffset.UtcNow, snapshot);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record DiskUsageCache(string Signature, DateTimeOffset CapturedAt, DiskUsageSnapshot Snapshot);

    private static long GetDirectoryBytes(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
        {
            return 0;
        }

        long total = 0;
        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsReparsePoint(file))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsReparsePoint(child)) directories.Push(child);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return total;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
