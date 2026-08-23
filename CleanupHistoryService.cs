using System.IO;
using System.Text.Json;

namespace Cleaner;

public sealed record CleanupHistoryEntry(DateTimeOffset Timestamp, string Scope, int DeletedFiles, long ReclaimedBytes);

public sealed class CleanupHistoryService
{
    private const int MaxEntries = 20;
    private readonly string _historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cleaner",
        "history.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CleanupHistoryEntry? LoadLatest()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return null;
            }

            var entries = JsonSerializer.Deserialize<List<CleanupHistoryEntry>>(File.ReadAllText(_historyPath));
            return entries?.OrderByDescending(entry => entry.Timestamp).FirstOrDefault();
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<CleanupHistoryEntry> LoadAll()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<CleanupHistoryEntry>>(File.ReadAllText(_historyPath))?
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaxEntries)
                .ToArray() ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public void Append(CleanupHistoryEntry entry)
    {
        try
        {
            var entries = new List<CleanupHistoryEntry>();
            if (File.Exists(_historyPath))
            {
                entries = JsonSerializer.Deserialize<List<CleanupHistoryEntry>>(File.ReadAllText(_historyPath)) ?? entries;
            }

            entries.Add(entry);
            var recent = entries.OrderByDescending(item => item.Timestamp).Take(MaxEntries).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(recent, JsonOptions));
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
