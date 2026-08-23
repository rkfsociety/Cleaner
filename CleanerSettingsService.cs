using System.IO;
using System.Text.Json;

namespace Cleaner;

public sealed class CleanerSettingsService
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cleaner",
        "settings.json");

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

    public void SaveSelectedDrives(IEnumerable<string> drives)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(drives.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
