using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Cleaner;

/// <summary>Программа, найденная в списке установленного ПО Windows.</summary>
public sealed record InstalledProgram(
    string Name,
    string Publisher,
    string Version,
    long EstimatedBytes,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastUsedAt,
    string LastUsedSource,
    string InstallLocation,
    string RegistryScope);

public enum ProgramSortMode
{
    LeastRecentlyUsed,
    MostRecentlyUsed,
    LargestFirst,
    NewestInstall,
    Name
}

/// <summary>
/// Читает установленные программы из реестра и оценивает давность их использования.
/// Ничего не изменяет и не удаляет: выполняется только чтение реестра и метаданных файлов.
/// </summary>
public sealed class InstalledProgramsService
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UserAssistKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private static readonly TimeSpan ReasonablePast = TimeSpan.FromDays(365 * 30);

    public Task<IReadOnlyList<InstalledProgram>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledProgram>>(() => Load(cancellationToken), cancellationToken);
    }

    public IReadOnlyList<InstalledProgram> Load(CancellationToken cancellationToken = default)
    {
        var usage = BuildUsageIndex();
        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, scope) in EnumerateUninstallScopes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in ReadUninstallEntries(hive, view, scope, cancellationToken))
            {
                var key = $"{entry.Name}|{entry.Version}|{entry.InstallLocation}";
                if (!seen.Add(key))
                {
                    continue;
                }

                programs.Add(Enrich(entry, usage));
            }
        }

        return Sort(programs, ProgramSortMode.LeastRecentlyUsed);
    }

    /// <summary>Сортировка списка; программы без данных о запуске всегда идут в конце.</summary>
    public static IReadOnlyList<InstalledProgram> Sort(IEnumerable<InstalledProgram> programs, ProgramSortMode mode)
    {
        var items = programs.ToList();
        return mode switch
        {
            ProgramSortMode.LeastRecentlyUsed => items
                .OrderBy(program => program.LastUsedAt.HasValue ? 0 : 1)
                .ThenBy(program => program.LastUsedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            ProgramSortMode.MostRecentlyUsed => items
                .OrderBy(program => program.LastUsedAt.HasValue ? 0 : 1)
                .ThenByDescending(program => program.LastUsedAt ?? DateTimeOffset.MinValue)
                .ThenBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            ProgramSortMode.LargestFirst => items
                .OrderByDescending(program => program.EstimatedBytes)
                .ThenBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            ProgramSortMode.NewestInstall => items
                .OrderByDescending(program => program.InstalledAt ?? DateTimeOffset.MinValue)
                .ThenBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            _ => items.OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
        };
    }

    /// <summary>Отбор по подстроке в названии или издателе и по числу дней без запуска.</summary>
    public static IReadOnlyList<InstalledProgram> Filter(IEnumerable<InstalledProgram> programs, string? search, int unusedDays, DateTimeOffset now)
    {
        var query = programs;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(program =>
                program.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                program.Publisher.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        if (unusedDays > 0)
        {
            var threshold = now - TimeSpan.FromDays(unusedDays);
            query = query.Where(program => program.LastUsedAt is null || program.LastUsedAt <= threshold);
        }

        return query.ToArray();
    }

    private static InstalledProgram Enrich(UninstallEntry entry, UsageIndex usage)
    {
        DateTimeOffset? lastUsed = usage.FindByLocation(entry.InstallLocation);
        var source = lastUsed is null ? "нет данных" : "журнал запусков";

        foreach (var name in CollectExecutableNames(entry))
        {
            if (IsSharedExecutableName(name))
            {
                continue;
            }

            var byName = usage.FindByExecutable(name);
            if (byName is not null && (lastUsed is null || byName > lastUsed))
            {
                lastUsed = byName;
                source = "журнал запусков";
            }
        }

        if (lastUsed is null && entry.InstalledAt is not null)
        {
            lastUsed = entry.InstalledAt;
            source = "дата установки";
        }

        return new InstalledProgram(
            entry.Name,
            entry.Publisher,
            entry.Version,
            entry.EstimatedBytes,
            entry.InstalledAt,
            lastUsed,
            source,
            entry.InstallLocation,
            entry.Scope);
    }

    private static IReadOnlyList<string> CollectExecutableNames(UninstallEntry entry)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.DisplayIcon))
        {
            var path = entry.DisplayIcon.Split(',')[0].Trim('"', ' ');
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    names.Add(Path.GetFileName(path));
                }
                catch (ArgumentException)
                {
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            try
            {
                if (Directory.Exists(entry.InstallLocation))
                {
                    foreach (var file in Directory.EnumerateFiles(entry.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly).Take(40))
                    {
                        names.Add(Path.GetFileName(file));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return names.ToArray();
    }

    /// <summary>
    /// Такие имена встречаются у десятков программ, поэтому сопоставление по имени файла
    /// дало бы чужую дату запуска. Для них остаётся только совпадение по полному пути установки.
    /// </summary>
    internal static bool IsSharedExecutableName(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(executable);
        return SharedExecutableNames.Contains(name) || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> SharedExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "install", "installer", "uninstall", "uninstaller", "update", "updater", "upgrade",
        "launcher", "launch", "start", "starter", "run", "main", "app", "application", "client",
        "service", "server", "helper", "host", "agent", "manager", "tray", "monitor", "config",
        "settings", "tool", "tools", "console", "cmd", "gui", "test", "vcredist", "crashpad_handler",
        "crashreporter", "elevate", "repair", "modify", "wizard"
    };

    private static IEnumerable<(RegistryHive Hive, RegistryView View, string Scope)> EnumerateUninstallScopes()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, "Все пользователи");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, "Все пользователи (32-бит)");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64, "Текущий пользователь");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32, "Текущий пользователь (32-бит)");
    }

    private static IEnumerable<UninstallEntry> ReadUninstallEntries(RegistryHive hive, RegistryView view, string scope, CancellationToken cancellationToken)
    {
        var entries = new List<UninstallEntry>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return entries;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    var entry = ReadEntry(key, scope);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (SecurityException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (SecurityException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return entries;
    }

    private static UninstallEntry? ReadEntry(RegistryKey? key, string scope)
    {
        if (key is null)
        {
            return null;
        }

        var name = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (ToInt(key.GetValue("SystemComponent")) == 1)
        {
            return null;
        }

        if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        if (key.GetValue("ReleaseType") is string releaseType &&
            (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var sizeKilobytes = ToInt(key.GetValue("EstimatedSize"));
        return new UninstallEntry(
            name.Trim(),
            (key.GetValue("Publisher") as string)?.Trim() ?? string.Empty,
            (key.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty,
            sizeKilobytes > 0 ? (long)sizeKilobytes * 1024 : 0,
            ParseInstallDate(key.GetValue("InstallDate") as string),
            (key.GetValue("InstallLocation") as string)?.Trim().Trim('"') ?? string.Empty,
            (key.GetValue("DisplayIcon") as string)?.Trim() ?? string.Empty,
            scope);
    }

    /// <summary>Дата установки хранится строкой вида <c>20240115</c>.</summary>
    internal static DateTimeOffset? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(value.Trim(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static int ToInt(object? value) => value switch
    {
        int number => number,
        long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
        string text when int.TryParse(text, out var parsed) => parsed,
        _ => 0
    };

    private static UsageIndex BuildUsageIndex()
    {
        var byPath = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var byExecutable = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, executedAt) in ReadUserAssist())
        {
            Remember(byPath, path, executedAt);
            try
            {
                Remember(byExecutable, Path.GetFileName(path), executedAt);
            }
            catch (ArgumentException)
            {
            }
        }

        foreach (var (executable, executedAt) in ReadPrefetch())
        {
            Remember(byExecutable, executable, executedAt);
        }

        return new UsageIndex(byPath, byExecutable);
    }

    private static void Remember(Dictionary<string, DateTimeOffset> map, string key, DateTimeOffset value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!map.TryGetValue(key, out var existing) || value > existing)
        {
            map[key] = value;
        }
    }

    /// <summary>Журнал запусков проводника (UserAssist) текущего пользователя.</summary>
    private static IEnumerable<(string Path, DateTimeOffset ExecutedAt)> ReadUserAssist()
    {
        var results = new List<(string, DateTimeOffset)>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(UserAssistKey);
            if (root is null)
            {
                return results;
            }

            foreach (var guid in root.GetSubKeyNames())
            {
                using var counts = root.OpenSubKey($@"{guid}\Count");
                if (counts is null)
                {
                    continue;
                }

                foreach (var valueName in counts.GetValueNames())
                {
                    if (counts.GetValue(valueName) is not byte[] data)
                    {
                        continue;
                    }

                    if (!TryReadLastExecuted(data, out var executedAt))
                    {
                        continue;
                    }

                    var path = ExpandUserAssistPath(Rot13(valueName));
                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add((path, executedAt));
                    }
                }
            }
        }
        catch (SecurityException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return results;
    }

    /// <summary>Имя файла Prefetch содержит имя программы, а время записи — момент последнего запуска.</summary>
    private static IEnumerable<(string Executable, DateTimeOffset ExecutedAt)> ReadPrefetch()
    {
        var results = new List<(string, DateTimeOffset)>();
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            if (!Directory.Exists(folder))
            {
                return results;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.pf", SearchOption.TopDirectoryOnly))
            {
                if (!TryReadPrefetchExecutable(Path.GetFileName(file), out var executable))
                {
                    continue;
                }

                results.Add((executable, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return results;
    }

    /// <summary>«CHROME.EXE-A1B2C3D4.pf» → «CHROME.EXE».</summary>
    internal static bool TryReadPrefetchExecutable(string prefetchFileName, out string executable)
    {
        executable = string.Empty;
        if (string.IsNullOrWhiteSpace(prefetchFileName) || !prefetchFileName.EndsWith(".pf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = prefetchFileName[..^3];
        var separator = name.LastIndexOf('-');
        if (separator <= 0)
        {
            return false;
        }

        executable = name[..separator];
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Значения UserAssist закодированы ROT13.</summary>
    internal static string Rot13(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var symbol in value)
        {
            if (symbol is >= 'a' and <= 'z')
            {
                builder.Append((char)('a' + (symbol - 'a' + 13) % 26));
            }
            else if (symbol is >= 'A' and <= 'Z')
            {
                builder.Append((char)('A' + (symbol - 'A' + 13) % 26));
            }
            else
            {
                builder.Append(symbol);
            }
        }

        return builder.ToString();
    }

    /// <summary>В записи UserAssist (Windows 7 и новее) время последнего запуска лежит по смещению 60 в формате FILETIME.</summary>
    internal static bool TryReadLastExecuted(byte[] data, out DateTimeOffset executedAt)
    {
        executedAt = default;
        if (data.Length < 68)
        {
            return false;
        }

        var fileTime = BitConverter.ToInt64(data, 60);
        if (fileTime <= 0)
        {
            return false;
        }

        try
        {
            var utc = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
            if (utc < DateTimeOffset.UtcNow - ReasonablePast || utc > DateTimeOffset.UtcNow.AddDays(1))
            {
                return false;
            }

            executedAt = utc;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Пути UserAssist содержат переменные окружения и идентификаторы известных папок.</summary>
    internal static string ExpandUserAssistPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var expanded = path;
        if (expanded.StartsWith('{'))
        {
            var end = expanded.IndexOf('}');
            if (end > 0 && KnownFolders.TryGetValue(expanded[..(end + 1)], out var folder))
            {
                var resolved = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(resolved))
                {
                    expanded = resolved + expanded[(end + 1)..];
                }
            }
        }

        try
        {
            return Environment.ExpandEnvironmentVariables(expanded);
        }
        catch (ArgumentException)
        {
            return expanded;
        }
    }

    private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.SpecialFolder.SystemX86,
        ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"] = Environment.SpecialFolder.Desktop,
        ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.SpecialFolder.LocalApplicationData,
        ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = Environment.SpecialFolder.ApplicationData,
        ["{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"] = Environment.SpecialFolder.MyDocuments
    };

    private sealed record UninstallEntry(
        string Name,
        string Publisher,
        string Version,
        long EstimatedBytes,
        DateTimeOffset? InstalledAt,
        string InstallLocation,
        string DisplayIcon,
        string Scope);

    private sealed class UsageIndex(Dictionary<string, DateTimeOffset> byPath, Dictionary<string, DateTimeOffset> byExecutable)
    {
        public DateTimeOffset? FindByExecutable(string executable)
        {
            return byExecutable.TryGetValue(executable, out var value) ? value : null;
        }

        public DateTimeOffset? FindByLocation(string installLocation)
        {
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                return null;
            }

            var prefix = installLocation.TrimEnd('\\') + "\\";
            DateTimeOffset? best = null;
            foreach (var (path, executedAt) in byPath)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (best is null || executedAt > best))
                {
                    best = executedAt;
                }
            }

            return best;
        }
    }
}
