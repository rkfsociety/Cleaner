using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class InstalledProgramsServiceTests
{
    private static InstalledProgram Program(string name, DateTimeOffset? lastUsed, long bytes = 0, DateTimeOffset? installed = null, string publisher = "")
    {
        return new InstalledProgram(
            name,
            publisher,
            "1.0",
            bytes,
            installed,
            lastUsed,
            lastUsed is null ? "нет данных" : "журнал запусков",
            string.Empty,
            "Все пользователи",
            @"C:\Program Files\App\unins000.exe",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App");
    }

    [Fact]
    public void Rot13_DecodesUserAssistValueName()
    {
        Assert.Equal(@"C:\Program Files\App\app.exe", InstalledProgramsService.Rot13(@"P:\Cebtenz Svyrf\Ncc\ncc.rkr"));
    }

    [Fact]
    public void Rot13_IsSymmetric()
    {
        const string value = "Cleaner Test 123";

        Assert.Equal(value, InstalledProgramsService.Rot13(InstalledProgramsService.Rot13(value)));
    }

    [Fact]
    public void TryReadLastExecuted_ReadsFileTimeFromOffset60()
    {
        var expected = new DateTimeOffset(2024, 5, 17, 10, 30, 0, TimeSpan.Zero);
        var data = new byte[72];
        BitConverter.GetBytes(expected.UtcDateTime.ToFileTimeUtc()).CopyTo(data, 60);

        Assert.True(InstalledProgramsService.TryReadLastExecuted(data, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(67)]
    public void TryReadLastExecuted_RejectsShortRecords(int length)
    {
        Assert.False(InstalledProgramsService.TryReadLastExecuted(new byte[length], out _));
    }

    [Fact]
    public void TryReadLastExecuted_RejectsEmptyTimestamp()
    {
        Assert.False(InstalledProgramsService.TryReadLastExecuted(new byte[72], out _));
    }

    [Fact]
    public void TryReadLastExecuted_RejectsImplausibleFutureTimestamp()
    {
        var data = new byte[72];
        BitConverter.GetBytes(DateTime.UtcNow.AddYears(5).ToFileTimeUtc()).CopyTo(data, 60);

        Assert.False(InstalledProgramsService.TryReadLastExecuted(data, out _));
    }

    [Theory]
    [InlineData("CHROME.EXE-A1B2C3D4.pf", "CHROME.EXE")]
    [InlineData("MY-APP.EXE-0011AABB.pf", "MY-APP.EXE")]
    public void TryReadPrefetchExecutable_ReadsName(string fileName, string expected)
    {
        Assert.True(InstalledProgramsService.TryReadPrefetchExecutable(fileName, out var executable));
        Assert.Equal(expected, executable);
    }

    [Theory]
    [InlineData("CHROME.EXE.pf")]
    [InlineData("AgAppLaunch.db")]
    [InlineData("")]
    public void TryReadPrefetchExecutable_RejectsForeignNames(string fileName)
    {
        Assert.False(InstalledProgramsService.TryReadPrefetchExecutable(fileName, out _));
    }

    [Fact]
    public void ParseInstallDate_ReadsRegistryFormat()
    {
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero).Date, InstalledProgramsService.ParseInstallDate("20240115")!.Value.Date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("15.01.2024")]
    [InlineData("20241332")]
    public void ParseInstallDate_ReturnsNull_OnUnknownFormat(string? value)
    {
        Assert.Null(InstalledProgramsService.ParseInstallDate(value));
    }

    [Fact]
    public void Sort_LeastRecentlyUsed_PutsOldestFirstAndUnknownLast()
    {
        var now = DateTimeOffset.Now;
        var programs = new[]
        {
            Program("Свежая", now.AddDays(-1)),
            Program("Без данных", null),
            Program("Старая", now.AddDays(-400))
        };

        var sorted = InstalledProgramsService.Sort(programs, ProgramSortMode.LeastRecentlyUsed);

        Assert.Equal(["Старая", "Свежая", "Без данных"], sorted.Select(program => program.Name));
    }

    [Fact]
    public void Sort_MostRecentlyUsed_PutsNewestFirstAndUnknownLast()
    {
        var now = DateTimeOffset.Now;
        var programs = new[]
        {
            Program("Старая", now.AddDays(-400)),
            Program("Без данных", null),
            Program("Свежая", now.AddDays(-1))
        };

        var sorted = InstalledProgramsService.Sort(programs, ProgramSortMode.MostRecentlyUsed);

        Assert.Equal(["Свежая", "Старая", "Без данных"], sorted.Select(program => program.Name));
    }

    [Fact]
    public void Sort_LargestFirst_OrdersByEstimatedSize()
    {
        var programs = new[]
        {
            Program("Маленькая", null, 1024),
            Program("Большая", null, 5 * 1024 * 1024)
        };

        var sorted = InstalledProgramsService.Sort(programs, ProgramSortMode.LargestFirst);

        Assert.Equal(["Большая", "Маленькая"], sorted.Select(program => program.Name));
    }

    [Fact]
    public void Filter_ByUnusedDays_KeepsIdleAndUnknownPrograms()
    {
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var programs = new[]
        {
            Program("Свежая", now.AddDays(-5)),
            Program("Забытая", now.AddDays(-200)),
            Program("Без данных", null)
        };

        var filtered = InstalledProgramsService.Filter(programs, null, 90, now);

        Assert.Equal(["Забытая", "Без данных"], filtered.Select(program => program.Name));
    }

    [Fact]
    public void Filter_BySearch_MatchesNameAndPublisher()
    {
        var now = DateTimeOffset.Now;
        var programs = new[]
        {
            Program("Notepad++", now, publisher: "Don Ho"),
            Program("7-Zip", now, publisher: "Igor Pavlov")
        };

        Assert.Equal(["Notepad++"], InstalledProgramsService.Filter(programs, "notepad", 0, now).Select(program => program.Name));
        Assert.Equal(["7-Zip"], InstalledProgramsService.Filter(programs, "pavlov", 0, now).Select(program => program.Name));
        Assert.Equal(2, InstalledProgramsService.Filter(programs, "  ", 0, now).Count);
    }

    [Theory]
    [InlineData("Update.exe")]
    [InlineData("setup.exe")]
    [InlineData("unins000.exe")]
    [InlineData("")]
    public void IsSharedExecutableName_DetectsGenericNames(string executable)
    {
        Assert.True(InstalledProgramsService.IsSharedExecutableName(executable));
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("BambuStudio.exe")]
    public void IsSharedExecutableName_KeepsProgramSpecificNames(string executable)
    {
        Assert.False(InstalledProgramsService.IsSharedExecutableName(executable));
    }

    [Fact]
    public void ExpandUserAssistPath_ResolvesKnownFolderIdentifier()
    {
        var expanded = InstalledProgramsService.ExpandUserAssistPath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\App\app.exe");

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"App\app.exe"), expanded);
    }

    [Fact]
    public void ExpandUserAssistPath_KeepsUnknownIdentifier()
    {
        const string path = @"{00000000-0000-0000-0000-000000000000}\App\app.exe";

        Assert.Equal(path, InstalledProgramsService.ExpandUserAssistPath(path));
    }
}
