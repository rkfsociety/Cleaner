using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class ResidualCleanupServiceTests
{
    [Fact]
    public void Delete_ReportsWhyMissingFileWasSkipped()
    {
        var root = Directory.CreateTempSubdirectory("cleaner-residual-test-");
        try
        {
            var missing = Path.Combine(root.FullName, "already-removed.tmp");
            var result = new ResidualCleanupService().Delete([new ResidualFile(missing, 12, root.FullName)]);

            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.Equal(1, result.SkippedByReason[CleanupSkipReason.NotFound]);
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(true);
            }
        }
    }

    [Theory]
    [InlineData("Mesh Enabler", "Mesh Enabler", "Mesh Enabler", true)]
    [InlineData("AutodeskMeshEnabler", "Mesh Enabler", "Autodesk Mesh Enabler", true)]
    [InlineData("OtherApplication", "Mesh Enabler", "Autodesk Mesh Enabler", false)]
    [InlineData("App", "App", "App", false)]
    public void IsRelatedName_RequiresSpecificProgramName(string candidate, string installLeaf, string programName, bool expected)
    {
        Assert.Equal(expected, ResidualCleanupService.IsRelatedName(candidate, installLeaf, programName));
    }

    [Fact]
    public void Delete_RemovesEmptyNestedDirectoriesBottomUp()
    {
        var root = Directory.CreateTempSubdirectory("cleaner-residual-nested-");
        try
        {
            var nested = Path.Combine(root.FullName, "a", "b", "c");
            Directory.CreateDirectory(nested);
            var filePath = Path.Combine(nested, "leftover.dat");
            File.WriteAllBytes(filePath, [1, 2, 3, 4]);

            var result = new ResidualCleanupService().Delete([new ResidualFile(filePath, 4, root.FullName)]);

            Assert.Equal(1, result.DeletedFiles);
            Assert.False(Directory.Exists(root.FullName));
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(true);
            }
        }
    }

    [Fact]
    public async Task DeleteAsync_ReportsProgressAndHonorsCancellation()
    {
        var root = Directory.CreateTempSubdirectory("cleaner-residual-async-");
        try
        {
            var first = Path.Combine(root.FullName, "first.dat");
            var second = Path.Combine(root.FullName, "second.dat");
            File.WriteAllBytes(first, [1, 2]);
            File.WriteAllBytes(second, [3, 4, 5]);

            var reports = new List<CleanupProgress>();
            var progress = new SyncProgress<CleanupProgress>(reports.Add);
            var completed = await new ResidualCleanupService().DeleteAsync(
                [new ResidualFile(first, 2, root.FullName)],
                CancellationToken.None,
                progress);

            Assert.Equal(1, completed.DeletedFiles);
            Assert.NotEmpty(reports);
            Assert.Equal(1, reports[^1].DeletedFiles);
            Assert.Equal(2, reports[^1].ReclaimedBytes);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ResidualCleanupService().DeleteAsync(
                    [new ResidualFile(second, 3, root.FullName)],
                    cts.Token));
            Assert.True(File.Exists(second));
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(true);
            }
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
