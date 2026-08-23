using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class HistoryView : UserControl
{
    private readonly CleanupHistoryService _historyService;
    private readonly AppDialogHost _dialog;

    public HistoryView(CleanupHistoryService historyService, AppDialogHost dialog)
    {
        InitializeComponent();
        _historyService = historyService;
        _dialog = dialog;
        Reload();
    }

    private void Reload()
    {
        var rows = _historyService.LoadAll().Select(entry => new HistoryRow(
            entry.Timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm"),
            entry.Scope,
            entry.DeletedFiles.ToString("N0"),
            ByteSizeFormatter.Format(entry.ReclaimedBytes))).ToArray();
        HistoryList.ItemsSource = rows;
        HistoryList.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            rows.Length > 10 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
        SummaryText.Text = rows.Length == 0
            ? "Очистки ещё не выполнялись."
            : $"Сохранено операций: {rows.Length:N0}. Хранится не больше последних 20.";
        ClearHistoryButton.IsEnabled = rows.Length > 0;
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyService.LoadAll().Count == 0)
        {
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            "Очистить историю",
            "Удалить всю историю очисток? Настройки приложения останутся без изменений.",
            "Очистить историю");
        if (!confirmed)
        {
            return;
        }

        if (_historyService.Clear())
        {
            Reload();
        }
        else
        {
            await _dialog.ShowMessageAsync("Cleaner", "Не удалось очистить историю. Закройте программы, которые могут использовать файл, и повторите попытку.");
        }
    }

    private sealed record HistoryRow(string Date, string Scope, string DeletedFiles, string Reclaimed);
}
