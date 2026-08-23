using System.IO;
using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class CleanupPolicyServiceTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"cleaner-policy-{Guid.NewGuid():N}.json");

    [Fact]
    public void LoadMinimumAgeHours_ReturnsZero_WhenFileMissing()
    {
        var service = new CleanupPolicyService(_tempFile);

        Assert.Equal(0, service.LoadMinimumAgeHours());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValue()
    {
        var service = new CleanupPolicyService(_tempFile);

        service.SaveMinimumAgeHours(48);

        Assert.Equal(48, service.LoadMinimumAgeHours());
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(168, 168)]
    [InlineData(1000, 168)]
    public void SaveMinimumAgeHours_ClampsToAllowedRange(int input, int expected)
    {
        var service = new CleanupPolicyService(_tempFile);

        service.SaveMinimumAgeHours(input);

        Assert.Equal(expected, service.LoadMinimumAgeHours());
    }

    [Fact]
    public void LoadMinimumAgeHours_ReturnsZero_OnCorruptFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tempFile)!);
        File.WriteAllText(_tempFile, "{ not valid json");

        var service = new CleanupPolicyService(_tempFile);

        Assert.Equal(0, service.LoadMinimumAgeHours());
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
