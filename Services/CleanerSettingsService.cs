using System.IO;
using System.Text.Json;

namespace Cleaner;

public sealed class CleanerSettingsService
{
    private readonly string _settingsPath;

    public CleanerSettingsService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cleaner",
        "settings.json"))
    {
    }

    internal CleanerSettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public IReadOnlyList<string> LoadSelectedDrives(IReadOnlyList<string> availableDrives)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return availableDrives;
            }

            var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_settingsPath));
            var selected = availableDrives.Where(drive => saved?.Contains(drive, StringComparer.OrdinalIgnoreCase) == true).ToArray();
            return selected.Length > 0 ? selected : availableDrives;
        }
        catch (JsonException) { return availableDrives; }
        catch (IOException) { return availableDrives; }
        catch (UnauthorizedAccessException) { return availableDrives; }
    }

    public IReadOnlyList<string> LoadSelectedDrives(IReadOnlyList<string> availableDrives, string defaultDrive)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return availableDrives.Contains(defaultDrive, StringComparer.OrdinalIgnoreCase)
                    ? [defaultDrive]
                    : availableDrives;
            }

            var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_settingsPath));
            var selected = availableDrives.Where(drive => saved?.Contains(drive, StringComparer.OrdinalIgnoreCase) == true).ToArray();
            return selected.Length > 0 ? selected : [defaultDrive];
        }
        catch (JsonException) { return [defaultDrive]; }
        catch (IOException) { return [defaultDrive]; }
        catch (UnauthorizedAccessException) { return [defaultDrive]; }
    }

    public bool SaveSelectedDrives(IEnumerable<string> drives)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(drives.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
