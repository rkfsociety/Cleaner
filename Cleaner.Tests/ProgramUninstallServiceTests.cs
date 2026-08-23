using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class ProgramUninstallServiceTests
{
    [Fact]
    public void Parse_ReadsQuotedPathWithArguments()
    {
        var command = ProgramUninstallService.Parse("\"C:\\Program Files\\App\\unins000.exe\" /SILENT");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\App\unins000.exe", command!.FileName);
        Assert.Equal("/SILENT", command.Arguments);
    }

    [Fact]
    public void Parse_ReadsUnquotedPathWithSpaces()
    {
        var command = ProgramUninstallService.Parse(@"C:\Program Files\App\uninstall.exe /S");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\App\uninstall.exe", command!.FileName);
        Assert.Equal("/S", command.Arguments);
    }

    [Fact]
    public void Parse_ReadsCommandWithoutArguments()
    {
        var command = ProgramUninstallService.Parse(@"C:\Windows\unins.exe");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Windows\unins.exe", command!.FileName);
        Assert.Equal(string.Empty, command.Arguments);
    }

    [Fact]
    public void Parse_ConvertsMsiInstallSwitchToUninstall()
    {
        var command = ProgramUninstallService.Parse("MsiExec.exe /I{2D1F0C57-A1B2-4C3D-9E4F-0123456789AB}");

        Assert.NotNull(command);
        Assert.Equal("MsiExec.exe", command!.FileName);
        Assert.Equal("/X{2D1F0C57-A1B2-4C3D-9E4F-0123456789AB}", command.Arguments);
    }

    [Fact]
    public void Parse_KeepsMsiUninstallSwitch()
    {
        var command = ProgramUninstallService.Parse("MsiExec.exe /X{2D1F0C57-A1B2-4C3D-9E4F-0123456789AB}");

        Assert.Equal("/X{2D1F0C57-A1B2-4C3D-9E4F-0123456789AB}", command!.Arguments);
    }

    [Fact]
    public void Parse_DoesNotTouchSwitchesOfOrdinaryUninstallers()
    {
        var command = ProgramUninstallService.Parse("\"C:\\App\\setup.exe\" /Install /Repair");

        Assert.Equal("/Install /Repair", command!.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    public void Parse_ReturnsNull_OnUnusableCommand(string? uninstallString)
    {
        Assert.Null(ProgramUninstallService.Parse(uninstallString));
    }

    [Fact]
    public void Display_JoinsFileNameAndArguments()
    {
        Assert.Equal("app.exe /S", new UninstallCommand("app.exe", "/S").Display);
        Assert.Equal("app.exe", new UninstallCommand("app.exe", string.Empty).Display);
    }

    [Fact]
    public void DescribeOutcome_ReportsSuccess_WhenProgramIsGone()
    {
        Assert.Contains("удалена", ProgramUninstallService.DescribeOutcome(0, false));
        Assert.Contains("удалена", ProgramUninstallService.DescribeOutcome(1602, false));
    }

    [Fact]
    public void DescribeOutcome_ReportsCancellation()
    {
        Assert.Contains("отменено", ProgramUninstallService.DescribeOutcome(1602, true));
    }

    [Theory]
    [InlineData(1641)]
    [InlineData(3010)]
    public void DescribeOutcome_ReportsRestartRequirement(int exitCode)
    {
        Assert.Contains("перезагрузка", ProgramUninstallService.DescribeOutcome(exitCode, true));
    }

    [Fact]
    public void DescribeOutcome_ShowsUnknownExitCode()
    {
        Assert.Contains("1603", ProgramUninstallService.DescribeOutcome(1603, true));
    }
}
