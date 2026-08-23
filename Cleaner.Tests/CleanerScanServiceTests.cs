using System.IO;
using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class CleanerScanServiceTests
{
    [Fact]
    public void NormalizeDrives_DedupesAndReducesToRoots()
    {
        var result = CleanerScanService.NormalizeDrives(new[] { "C:\\Users\\foo", "c:\\", "D:\\", "D:\\data" });

        Assert.Equal(2, result.Count);
        Assert.Contains("C:\\", result, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("D:\\", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeDrives_IgnoresBlankEntries()
    {
        var result = CleanerScanService.NormalizeDrives(new[] { "", "  ", "C:\\" });

        Assert.Single(result);
    }

    [Fact]
    public void BuildWindowsTempRoots_ReturnsEmpty_WhenSystemDriveNotSelected()
    {
        var result = CleanerScanService.BuildWindowsTempRoots(new[] { "Z:\\" });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildWindowsTempRoots_ReturnsOnlySystemTemp_WhenSystemDriveSelected()
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        var result = CleanerScanService.BuildWindowsTempRoots(new[] { systemRoot });

        Assert.True(result.Count <= 1);
        Assert.All(result, root => Assert.EndsWith("Windows\\Temp", root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeleteFiles_SkipsFilesOutsideRoots()
    {
        using var scope = new TempScope();
        var outsidePath = scope.CreateFile("outside.tmp");
        var files = new[] { new ScanFile(outsidePath, 10) };

        var result = CleanerScanService.DeleteFiles(files, new[] { scope.CreateSubdirectory("allowed") }, minimumAgeHours: 0, CancellationToken.None);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public void DeleteFiles_DeletesFilesInsideRootsAndSumsReclaimedBytes()
    {
        using var scope = new TempScope();
        var root = scope.CreateSubdirectory("allowed");
        var filePath = Path.Combine(root, "a.tmp");
        File.WriteAllBytes(filePath, new byte[16]);

        var files = new[] { new ScanFile(filePath, 16) };

        var result = CleanerScanService.DeleteFiles(files, new[] { root }, minimumAgeHours: 0, CancellationToken.None);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(16, result.ReclaimedBytes);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteFiles_SkipsRecentlyWrittenFiles_WhenMinimumAgeSet()
    {
        using var scope = new TempScope();
        var root = scope.CreateSubdirectory("allowed");
        var filePath = Path.Combine(root, "fresh.tmp");
        File.WriteAllBytes(filePath, new byte[4]);

        var files = new[] { new ScanFile(filePath, 4) };

        var result = CleanerScanService.DeleteFiles(files, new[] { root }, minimumAgeHours: 24, CancellationToken.None);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void DeleteFiles_SkipsMissingFiles()
    {
        using var scope = new TempScope();
        var root = scope.CreateSubdirectory("allowed");
        var missingPath = Path.Combine(root, "gone.tmp");

        var files = new[] { new ScanFile(missingPath, 4) };

        var result = CleanerScanService.DeleteFiles(files, new[] { root }, minimumAgeHours: 0, CancellationToken.None);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
    }

    private sealed class TempScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cleaner-test-{Guid.NewGuid():N}");

        public TempScope()
        {
            Directory.CreateDirectory(_root);
        }

        public string CreateSubdirectory(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string name)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[4]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
