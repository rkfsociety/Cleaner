using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class HistoryWindow : Window
{
    private readonly CleanupHistoryService _historyService;

    public HistoryWindow(CleanupHistoryService historyService)
    {
        InitializeComponent();
        _historyService = historyService;
        Reload();
    }

    private void Reload()
    {
        var rows = _historyService.LoadAll().Select(entry => new HistoryRow(
            entry.Timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm"),
            entry.Scope,
            entry.DeletedFiles.ToString("N0"),
            FormatBytes(entry.ReclaimedBytes))).ToArray();
        HistoryList.ItemsSource = rows;
        HistoryList.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            rows.Length > 10 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyService.LoadAll().Count == 0)
        {
            return;
        }

        var result = MessageBox.Show("Удалить всю историю очисток? Настройки приложения останутся без изменений.", "Очистить историю", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            if (_historyService.Clear())
            {
                Reload();
            }
            else
            {
                MessageBox.Show("Не удалось очистить историю. Закройте программы, которые могут использовать файл, и повторите попытку.", "Cleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static string FormatBytes(long bytes) => ByteSizeFormatter.Format(bytes);

    private sealed record HistoryRow(string Date, string Scope, string DeletedFiles, string Reclaimed);
}
