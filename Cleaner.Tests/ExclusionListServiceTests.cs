using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public sealed class ExclusionListServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cleaner-excl-{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndLoad_NormalizesAndDeduplicatesPaths()
    {
        var path = Path.Combine(_root, "exclusions.json");
        var service = new ExclusionListService(path);

        var saved = service.Save([@"C:\Temp\Keep\", @"c:\temp\keep", @"C:\Temp\file.log"]);
        Assert.True(saved);

        var loaded = service.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(@"C:\Temp\Keep", loaded[0]);
        Assert.Equal(@"C:\Temp\file.log", loaded[1]);
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenFileCorrupt()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "exclusions.json");
        File.WriteAllText(path, "{invalid");
        var service = new ExclusionListService(path);

        Assert.Empty(service.Load());
    }

    [Fact]
    public void IsExcluded_MatchesExactFileAndSubfolderWithoutPrefixCollision()
    {
        var exclusions = new[] { @"C:\Temp\Keep", @"C:\Temp\single.log" };

        Assert.True(ExclusionListService.IsExcluded(@"C:\Temp\Keep\a.txt", exclusions));
        Assert.True(ExclusionListService.IsExcluded(@"c:\temp\keep\sub\b.txt", exclusions));
        Assert.True(ExclusionListService.IsExcluded(@"C:\Temp\single.log", exclusions));

        Assert.False(ExclusionListService.IsExcluded(@"C:\Temp\KeepOther\a.txt", exclusions));
        Assert.False(ExclusionListService.IsExcluded(@"C:\Temp\single.log.bak", exclusions));
    }

    [Fact]
    public void NormalizePath_TrimsTrailingSeparatorAndHandlesInvalidPaths()
    {
        Assert.Equal(@"C:\Temp\Keep", ExclusionListService.NormalizePath(@"  C:\Temp\Keep\  "));
        Assert.Equal(@"C:\Temp\Keep", ExclusionListService.NormalizePath(@"C:\Temp\Keep"));
        Assert.Equal(@"C:\", ExclusionListService.NormalizePath(@"C:\"));
        Assert.Null(ExclusionListService.NormalizePath("   "));
        Assert.Null(ExclusionListService.NormalizePath("bad\0path"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
