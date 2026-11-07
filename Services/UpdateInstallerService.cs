using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace Cleaner;

public sealed class UpdateInstallerService
{
    private const string ProductDirectory = "Cleaner";
    private const string InstallerResourceName = "Cleaner.ApplyUpdate.ps1";

    public string TargetExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductDirectory,
        "Cleaner.exe");

    public string CurrentExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Не удалось определить путь запущенного Cleaner.exe.");

    public bool IsInstalled => PathsEqual(CurrentExecutablePath, TargetExecutablePath);

    public bool CanInstallCurrentExecutable =>
        !IsInstalled &&
        File.Exists(CurrentExecutablePath) &&
        string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    public string InstallFailureMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductDirectory,
        "update-install-error.txt");

    public string StageCurrentExecutable()
    {
        if (!CanInstallCurrentExecutable)
        {
            throw new InvalidOperationException("Установить можно только автономный Cleaner.exe, запущенный не из папки установки.");
        }

        var stagingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirectory,
            "updates",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagedPath = Path.Combine(stagingDirectory, "Cleaner.exe");
        File.Copy(CurrentExecutablePath, stagedPath);
        return stagedPath;
    }

    public void StartInstall(string stagedExecutablePath)
    {
        var targetPath = Path.GetFullPath(TargetExecutablePath);
        var packagePath = Path.GetFullPath(stagedExecutablePath);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Не найден подготовленный файл Cleaner.exe.", packagePath);
        }

        var stagingRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectory, "updates"));
        if (!packagePath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Файл установки находится вне временной папки обновлений Cleaner.");
        }

        var localData = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectory));
        var helperDirectory = Path.Combine(localData, "installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        var helperPath = Path.Combine(helperDirectory, "apply-update.ps1");
        using (var resource = typeof(UpdateInstallerService).Assembly.GetManifestResourceStream(InstallerResourceName)
            ?? throw new InvalidOperationException("Не найден встроенный помощник установки Cleaner."))
        using (var file = File.Create(helperPath))
        {
            resource.CopyTo(file);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", helperPath,
            "-ProcessId", Environment.ProcessId.ToString(),
            "-PackagePath", packagePath,
            "-TargetPath", targetPath,
            "-ShortcutPath", GetStartMenuShortcutPath(),
            "-FallbackPath", CurrentExecutablePath,
            "-FailureMarkerPath", InstallFailureMarkerPath,
            "-LogPath", Path.Combine(localData, "app.log")
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить помощник установки Cleaner.");
            if (process.WaitForExit(500))
            {
                throw new InvalidOperationException($"Помощник установки Cleaner завершился до начала обновления (код {process.ExitCode}).");
            }
        }
        catch
        {
            DeleteHelperDirectory(helperDirectory);
            throw;
        }
    }

    public void DeleteStagedExecutable(string stagedExecutablePath)
    {
        try
        {
            var stagingRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectory, "updates"));
            var packagePath = Path.GetFullPath(stagedExecutablePath);
            if (!packagePath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directory = Path.GetDirectoryName(packagePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException exception) { AppLogService.Write("Не удалось удалить подготовленные файлы установки", exception); }
        catch (UnauthorizedAccessException exception) { AppLogService.Write("Нет доступа для удаления подготовленных файлов установки", exception); }
        catch (ArgumentException exception) { AppLogService.Write("Некорректный путь подготовленных файлов установки", exception); }
    }

    public string? ConsumeInstallFailure()
    {
        if (!File.Exists(InstallFailureMarkerPath))
        {
            return null;
        }

        try
        {
            var message = File.ReadAllText(InstallFailureMarkerPath, Encoding.UTF8).Trim();
            File.Delete(InstallFailureMarkerPath);
            return string.IsNullOrWhiteSpace(message) ? "Cleaner не удалось установить. Запущена предыдущая версия." : message;
        }
        catch (IOException)
        {
            return "Cleaner не удалось установить. Запущена предыдущая версия.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Cleaner не удалось установить. Запущена предыдущая версия.";
        }
    }

    private static string GetStartMenuShortcutPath()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return Path.Combine(programs, "Cleaner.lnk");
    }

    private static void DeleteHelperDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException exception) { AppLogService.Write("Не удалось удалить временный скрипт установки", exception); }
        catch (UnauthorizedAccessException exception) { AppLogService.Write("Нет доступа для удаления временного скрипта установки", exception); }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
