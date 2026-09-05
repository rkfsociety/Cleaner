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
}
