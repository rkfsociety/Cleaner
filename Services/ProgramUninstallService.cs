using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Cleaner;

/// <summary>Разобранная команда удаления: что запускать и с какими аргументами.</summary>
public sealed record UninstallCommand(string FileName, string Arguments)
{
    public string Display => string.IsNullOrWhiteSpace(Arguments) ? FileName : $"{FileName} {Arguments}";
}

/// <summary>Итог запуска деинсталлятора.</summary>
public sealed record UninstallOutcome(
    bool Started,
    int? ExitCode,
    bool StillInstalled,
    string Message,
    IReadOnlyList<ResidualFile> ResidualFiles,
    IReadOnlyList<ResidualRegistryEntry> ResidualRegistryEntries);

/// <summary>
/// Запускает штатный деинсталлятор программы. Cleaner сам ничего не удаляет:
/// файлы и записи реестра убирает деинсталлятор производителя, а пользователь
/// подтверждает удаление в его собственном окне.
/// </summary>
public sealed class ProgramUninstallService
{
    private readonly InstalledProgramsService _programsService;
    private readonly ResidualCleanupService _residuals;

    public ProgramUninstallService() : this(new InstalledProgramsService(), new ResidualCleanupService())
    {
    }

    public ProgramUninstallService(InstalledProgramsService programsService, ResidualCleanupService? residuals = null)
    {
        _programsService = programsService;
        _residuals = residuals ?? new ResidualCleanupService();
    }

    /// <summary>
    /// Разбирает строку <c>UninstallString</c> из реестра на исполняемый файл и аргументы.
    /// Для MSI команда приводится к удалению (<c>/X</c>), иначе msiexec открыл бы восстановление.
    /// </summary>
    public static UninstallCommand? Parse(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var text = uninstallString.Trim();
        string fileName;
        string arguments;

        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing <= 1)
            {
                return null;
            }

            fileName = text[1..closing];
            arguments = text[(closing + 1)..].Trim();
        }
        else
        {
            var executableEnd = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                fileName = text[..(executableEnd + 4)];
                arguments = text[(executableEnd + 4)..].Trim();
            }
            else
            {
                var space = text.IndexOf(' ');
                fileName = space > 0 ? text[..space] : text;
                arguments = space > 0 ? text[(space + 1)..].Trim() : string.Empty;
            }
        }

        fileName = fileName.Trim();
        if (fileName.Length == 0)
        {
            return null;
        }

        if (IsMsiExec(fileName))
        {
            arguments = ToMsiUninstallArguments(arguments);
        }

        return new UninstallCommand(fileName, arguments);
    }

    private static bool IsMsiExec(string fileName)
    {
        try
        {
            return string.Equals(Path.GetFileNameWithoutExtension(fileName), "msiexec", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>«/I{GUID}» — это установка или восстановление; для удаления нужен ключ «/X».</summary>
    internal static string ToMsiUninstallArguments(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length >= 2 && (part[0] == '/' || part[0] == '-') && (part[1] == 'I' || part[1] == 'i'))
            {
                parts[index] = $"/X{part[2..]}";
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Запускает деинсталлятор и ждёт его завершения. Возвращает результат вместе с проверкой,
    /// осталась ли программа в списке установленных.
    /// </summary>
    public async Task<UninstallOutcome> RunAsync(InstalledProgram program, CancellationToken cancellationToken = default)
    {
        var command = Parse(program.UninstallString);
        if (command is null)
        {
            return new UninstallOutcome(false, null, true, "В реестре нет команды удаления для этой программы.", _residuals.Find(program), _residuals.FindRegistryEntries(program));
        }

        try
        {
            var startInfo = new ProcessStartInfo(command.FileName)
            {
                Arguments = command.Arguments,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
            {
                startInfo.WorkingDirectory = program.InstallLocation;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return BuildOutcome(program, false, null, "Не удалось запустить деинсталлятор.");
            }

            await process.WaitForExitAsync(cancellationToken);
            var exitCode = process.ExitCode;
            var stillInstalled = _programsService.IsStillInstalled(program);
            return BuildOutcome(program, true, exitCode, DescribeOutcome(exitCode, stillInstalled));
        }
        catch (Win32Exception exception)
        {
            AppLogService.Write($"Не удалось запустить деинсталлятор: {command.Display}", exception);
            return BuildOutcome(program, false, null, "Windows не разрешила запуск деинсталлятора или он не найден.");
        }
        catch (FileNotFoundException exception)
        {
            AppLogService.Write($"Деинсталлятор не найден: {command.Display}", exception);
            return BuildOutcome(program, false, null, "Файл деинсталлятора не найден. Возможно, программа уже удалена частично.");
        }
        catch (InvalidOperationException exception)
        {
            AppLogService.Write($"Ошибка запуска деинсталлятора: {command.Display}", exception);
            return BuildOutcome(program, false, null, "Не удалось запустить деинсталлятор.");
        }
    }

    private UninstallOutcome BuildOutcome(InstalledProgram program, bool started, int? exitCode, string message)
    {
        return new UninstallOutcome(started, exitCode, _programsService.IsStillInstalled(program), message,
            _residuals.Find(program), _residuals.FindRegistryEntries(program));
    }

    /// <summary>Понятное сообщение по коду возврата деинсталлятора и состоянию реестра.</summary>
    internal static string DescribeOutcome(int? exitCode, bool stillInstalled)
    {
        if (!stillInstalled)
        {
            return "Программа удалена и больше не числится установленной.";
        }

        return exitCode switch
        {
            0 => "Деинсталлятор завершился без ошибок, но запись в реестре осталась. Иногда она исчезает после перезагрузки.",
            1602 => "Удаление отменено пользователем.",
            1641 or 3010 => "Программа удалена, требуется перезагрузка компьютера.",
            null => "Деинсталлятор завершился, состояние программы уточните повторной проверкой.",
            _ => $"Деинсталлятор завершился с кодом {exitCode}. Программа осталась в списке установленных."
        };
    }
}
