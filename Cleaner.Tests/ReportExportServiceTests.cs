using System.Text;
using System.Text.Json;
using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public sealed class ReportExportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cleaner-export-{Guid.NewGuid():N}");

    [Fact]
    public void ExportProgramsCsv_EscapesCommasQuotesAndIncludesArchitectureAndRegistryInfo()
    {
        var file = Path.Combine(_root, "programs.csv");
        var programs = new[]
        {
            new InstalledProgram(
                "App, \"Pro\"",
                "Acme, Inc.",
                "2.0",
                1024,
                new DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero),
                null,
                "дата установки",
                @"C:\Program Files (x86)\App",
                "Все пользователи (32-бит)",
                @"msiexec /x {123}",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\App")
        };

        ReportExportService.ExportProgramsCsv(file, programs);

        var content = File.ReadAllText(file, Encoding.UTF8);
        Assert.Contains("\"App, \"\"Pro\"\"\"", content);
        Assert.Contains("\"Acme, Inc.\"", content);
        Assert.Contains(",x86,", content);
        Assert.Contains("msiexec /x {123}", content);
    }

    [Fact]
    public void ExportProgramsJson_WritesValidJsonWithExpectedFields()
    {
        var file = Path.Combine(_root, "programs.json");
        var programs = new[]
        {
            new InstalledProgram(
                "Cleaner",
                "Test",
                "1.0",
                2048,
                null,
                null,
                "нет данных",
                "",
                "Все пользователи",
                "unins.exe",
                @"HKEY_LOCAL_MACHINE\...\Cleaner")
        };

        ReportExportService.ExportProgramsJson(file, programs);

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var first = doc.RootElement[0];
        Assert.Equal("Cleaner", first.GetProperty("Name").GetString());
        Assert.Equal("x64", first.GetProperty("Architecture").GetString());
        Assert.Equal("unins.exe", first.GetProperty("UninstallString").GetString());
    }

    [Fact]
    public void ExportScanFilesAndHistory_WriteNonEmptyCsvAndJson()
    {
        var scanCsv = Path.Combine(_root, "scan.csv");
        var scanJson = Path.Combine(_root, "scan.json");
        var historyCsv = Path.Combine(_root, "history.csv");
        var historyJson = Path.Combine(_root, "history.json");

        var files = new[] { new ScanFile(@"C:\Temp\a.tmp", 512, "Кэш NuGet") };
        var history = new[] { new CleanupHistoryEntry(DateTimeOffset.Now, "Пользовательский Temp", 5, 1024) };

        ReportExportService.ExportScanFilesCsv(scanCsv, files);
        ReportExportService.ExportScanFilesJson(scanJson, files);
        ReportExportService.ExportHistoryCsv(historyCsv, history);
        ReportExportService.ExportHistoryJson(historyJson, history);

        Assert.Contains("Кэш NuGet", File.ReadAllText(scanCsv));
        Assert.Contains("Кэш NuGet", File.ReadAllText(scanJson));
        Assert.Contains("Пользовательский Temp", File.ReadAllText(historyCsv));
        Assert.Contains("Пользовательский Temp", File.ReadAllText(historyJson));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
