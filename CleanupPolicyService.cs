using System.IO;
using System.Text.Json;

namespace Cleaner;

public sealed class CleanupPolicyService
{
    private readonly string _policyPath;

    public CleanupPolicyService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cleaner",
        "policy.json"))
    {
    }

    internal CleanupPolicyService(string policyPath)
    {
        _policyPath = policyPath;
    }

    public int LoadMinimumAgeHours()
    {
        try
        {
            if (!File.Exists(_policyPath))
            {
                return 0;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_policyPath));
            return Math.Clamp(document.RootElement.GetProperty("minimumAgeHours").GetInt32(), 0, 168);
        }
        catch (JsonException) { return 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (KeyNotFoundException) { return 0; }
    }

    public void SaveMinimumAgeHours(int hours)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_policyPath)!);
            var value = new { minimumAgeHours = Math.Clamp(hours, 0, 168) };
            File.WriteAllText(_policyPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
