using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class ResidualCleanupServiceTests
{
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
