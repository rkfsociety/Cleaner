using System.IO;
using System.IO.Enumeration;
using System.Security;
using Microsoft.Win32;

namespace Cleaner;

/// <summary>Файл, который можно предложить удалить как остаток программы.</summary>
public sealed record ResidualFile(string Path, long Size, string Root);
public sealed record ResidualRegistryEntry(RegistryHive Hive, RegistryView View, string KeyPath, string DisplayName);

/// <summary>
/// Ищет остатки только внутри каталога установки и явно связанных папок профиля.
/// Поиск ничего не удаляет.
/// </summary>
public sealed class ResidualCleanupService
{
    private const int MaxFiles = 100_000;

    public IReadOnlyList<ResidualFile> Find(InstalledProgram program)
    {
        var roots = GetRoots(program);
        var files = new List<ResidualFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            EnumerateFiles(root, root, files, seen);
            if (files.Count >= MaxFiles)
            {
                break;
            }
        }

        return files;
    }

    public ResidualCleanupResult Delete(IEnumerable<ResidualFile> files)
    {
        var fileList = files as IReadOnlyList<ResidualFile> ?? files.ToArray();
        var deleted = 0;
        var skipped = 0;
        long bytes = 0;
        var skippedByReason = new Dictionary<CleanupSkipReason, int>();

        foreach (var file in fileList)
        {
            var attempt = WindowsSafeDeleteService.TryDelete(file.Path, file.Root);
            if (attempt.Succeeded)
            {
                deleted++;
                bytes += file.Size;
            }
            else
            {
                skipped++;
                skippedByReason[attempt.Reason] = skippedByReason.GetValueOrDefault(attempt.Reason) + 1;
            }
        }

        RemoveEmptyDirectories(fileList);
        return new ResidualCleanupResult(deleted, skipped, bytes, skippedByReason);
    }

    public IReadOnlyList<ResidualRegistryEntry> FindRegistryEntries(InstalledProgram program)
    {
        var results = new List<ResidualRegistryEntry>();
        foreach (var (hive, view) in RegistryScopes())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    var displayName = key?.GetValue("DisplayName") as string;
                    var installLocation = (key?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                    var exactKey = string.Equals(key?.Name, program.RegistryKeyPath, StringComparison.OrdinalIgnoreCase);
                    var exactProgram = string.Equals(displayName?.Trim(), program.Name, StringComparison.OrdinalIgnoreCase) &&
                                       (exactKey || SameLocation(installLocation, program.InstallLocation));
                    if (string.IsNullOrWhiteSpace(displayName) || !exactProgram)
                    {
                        continue;
                    }

                    results.Add(new ResidualRegistryEntry(hive, view, key!.Name, displayName.Trim()));
                }
            }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return results.GroupBy(entry => $"{entry.Hive}:{entry.View}:{entry.KeyPath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
    }

    public ResidualRegistryCleanupResult DeleteRegistryEntries(IEnumerable<ResidualRegistryEntry> entries)
    {
        var deleted = 0;
        var skipped = 0;
        var skippedByReason = new Dictionary<CleanupSkipReason, int>();
        foreach (var entry in entries)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                var name = Path.GetFileName(entry.KeyPath.TrimEnd('\\'));
                if (uninstall is null || string.IsNullOrWhiteSpace(name))
                {
                    skipped++;
                    skippedByReason[CleanupSkipReason.NotFound] = skippedByReason.GetValueOrDefault(CleanupSkipReason.NotFound) + 1;
                    continue;
                }

                using var key = uninstall.OpenSubKey(name);
                var displayName = key?.GetValue("DisplayName") as string;
                if (!string.Equals(displayName?.Trim(), entry.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    skippedByReason[CleanupSkipReason.Changed] = skippedByReason.GetValueOrDefault(CleanupSkipReason.Changed) + 1;
                    continue;
                }

                uninstall.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                deleted++;
            }
            catch (SecurityException) { skipped++; skippedByReason[CleanupSkipReason.AccessDenied] = skippedByReason.GetValueOrDefault(CleanupSkipReason.AccessDenied) + 1; }
            catch (UnauthorizedAccessException) { skipped++; skippedByReason[CleanupSkipReason.AccessDenied] = skippedByReason.GetValueOrDefault(CleanupSkipReason.AccessDenied) + 1; }
            catch (IOException) { skipped++; skippedByReason[CleanupSkipReason.Unknown] = skippedByReason.GetValueOrDefault(CleanupSkipReason.Unknown) + 1; }
        }

        return new ResidualRegistryCleanupResult(deleted, skipped, skippedByReason);
    }

    internal static IReadOnlyList<string> GetRoots(InstalledProgram program)
    {
        var roots = new List<string>();
        AddInstallRoot(roots, program.InstallLocation);

        var installLeaf = GetLeaf(program.InstallLocation);
        if (string.IsNullOrWhiteSpace(installLeaf))
        {
            return roots;
        }

        var profileRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        foreach (var profileRoot in profileRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(profileRoot) || !Directory.Exists(profileRoot))
            {
                continue;
            }

            FindRelatedDirectories(profileRoot, installLeaf, program.Name, roots);
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddInstallRoot(List<string> roots, string path)
    {
        if (TryNormalizeDirectory(path, out var normalized) && !IsDriveRoot(normalized))
        {
            roots.Add(normalized);
        }
    }

    private static void FindRelatedDirectories(string profileRoot, string installLeaf, string programName, List<string> roots)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(profileRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(directory) || !IsRelatedName(Path.GetFileName(directory), installLeaf, programName))
                {
                    continue;
                }

                roots.Add(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool IsRelatedName(string candidate, string installLeaf, string programName)
    {
        var candidateKey = NormalizeName(candidate);
        if (candidateKey.Length < 4)
        {
            return false;
        }

        var installKey = NormalizeName(installLeaf);
        if (candidateKey == installKey || candidateKey.Contains(installKey, StringComparison.OrdinalIgnoreCase) || installKey.Contains(candidateKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var words = programName.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeName)
            .Where(word => word.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return words.Length > 0 && words.All(candidateKey.Contains);
    }

    private static readonly EnumerationOptions ResidualEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static void EnumerateFiles(string directory, string root, List<ResidualFile> files, HashSet<string> seen)
    {
        if (files.Count >= MaxFiles || !Directory.Exists(directory) || IsReparsePoint(directory))
        {
            return;
        }

        try
        {
            var enumerable = new FileSystemEnumerable<ResidualFile>(
                directory,
                (ref FileSystemEntry entry) => new ResidualFile(entry.ToFullPath(), entry.Length, root),
                ResidualEnumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };

            foreach (var item in enumerable)
            {
                if (files.Count >= MaxFiles)
                {
                    break;
                }

                if (seen.Add(item.Path))
                {
                    files.Add(item);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void RemoveEmptyDirectories(IEnumerable<ResidualFile> files)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var rootPrefix = file.Root.TrimEnd(Path.DirectorySeparatorChar);
            var current = Path.GetDirectoryName(file.Path);
            while (!string.IsNullOrWhiteSpace(current))
            {
                var trimmed = current.TrimEnd(Path.DirectorySeparatorChar);
                if (!trimmed.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                candidates.Add(trimmed);
                if (string.Equals(trimmed, rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = Path.GetDirectoryName(trimmed);
            }
        }

        foreach (var directory in candidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if (!IsReparsePoint(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetLeaf(string path)
    {
        try { return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch (ArgumentException) { return string.Empty; }
    }

    private static string NormalizeName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryNormalizeDirectory(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return Path.IsPathRooted(normalized) && Directory.Exists(normalized) && !IsReparsePoint(normalized);
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }

    private static bool IsDriveRoot(string path) => string.Equals(Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.OrdinalIgnoreCase);

    private static bool SameLocation(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> RegistryScopes()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}

public enum CleanupSkipReason
{
    None,
    AccessDenied,
    FileInUse,
    NotFound,
    ReparsePoint,
    Changed,
    Unknown
}

public sealed record ResidualCleanupResult(int DeletedFiles, int SkippedFiles, long DeletedBytes, IReadOnlyDictionary<CleanupSkipReason, int> SkippedByReason);
public sealed record ResidualRegistryCleanupResult(int DeletedEntries, int SkippedEntries, IReadOnlyDictionary<CleanupSkipReason, int> SkippedByReason);
