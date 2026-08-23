using System.Windows;

namespace Cleaner;

public partial class HistoryWindow : Window
{
    public HistoryWindow(IEnumerable<CleanupHistoryEntry> entries)
    {
        InitializeComponent();
        HistoryList.ItemsSource = entries.Select(entry => new HistoryRow(
            entry.Timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm"),
            entry.Scope,
            entry.DeletedFiles.ToString("N0"),
            FormatBytes(entry.ReclaimedBytes)));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private sealed record HistoryRow(string Date, string Scope, string DeletedFiles, string Reclaimed);
}
