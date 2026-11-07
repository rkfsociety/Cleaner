using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public sealed class PersistenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cleaner-persistence-{Guid.NewGuid():N}");

    [Fact]
    public void Settings_FallsBackToSystemDrive_WhenSavedFileIsCorrupt()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "broken");
        var service = new CleanerSettingsService(path);

        var selected = service.LoadSelectedDrives(["C:\\", "D:\\"], "C:\\");

        Assert.Equal(["C:\\"], selected);
    }

    [Fact]
    public void History_ReturnsEmptyAndClearReportsSuccess_WhenFileIsCorrupt()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "history.json");
        File.WriteAllText(path, "broken");
        var service = new CleanupHistoryService(path);

        Assert.Empty(service.LoadAll());
        Assert.True(service.Clear());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EmbeddedApplyUpdateScript_HasUtf8BomForWindowsPowerShell51()
    {
        using var stream = typeof(UpdateInstallerService).Assembly.GetManifestResourceStream("Cleaner.ApplyUpdate.ps1");
        Assert.NotNull(stream);
        var preamble = new byte[3];
        Assert.Equal(3, stream!.Read(preamble, 0, 3));
        Assert.Equal([0xEF, 0xBB, 0xBF], preamble);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
