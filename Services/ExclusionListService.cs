using System.IO;
using System.Text.Json;

namespace Cleaner;

/// <summary>
/// Хранит постоянный белый список исключённых файлов и папок в %LOCALAPPDATA%\Cleaner\exclusions.json.
/// </summary>
public sealed class ExclusionListService
{
    private readonly string _exclusionsPath;

    public ExclusionListService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cleaner",
        "exclusions.json"))
    {
    }

    internal ExclusionListService(string exclusionsPath)
    {
        _exclusionsPath = exclusionsPath;
    }

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_exclusionsPath))
            {
                return [];
            }

            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_exclusionsPath));
            return Normalize(items ?? []);
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public bool Save(IEnumerable<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_exclusionsPath)!);
            var normalized = Normalize(paths);
            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_exclusionsPath, json);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsExcluded(string filePath, IReadOnlyList<string> exclusions)
    {
        if (exclusions.Count == 0 || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string fullFilePath;
        try
        {
            fullFilePath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var raw in exclusions)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullFilePath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = normalized + Path.DirectorySeparatorChar;
            if (fullFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Приводит путь к каноническому виду без завершающего разделителя (кроме корня диска).</summary>
    public static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(raw.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 2 && full[1] == ':')
            {
                full += Path.DirectorySeparatorChar;
            }

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var normalized = NormalizePath(raw);
            if (normalized is not null && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
