using System.IO;
using System.Text;
using System.Windows;

namespace Cleaner;

public partial class MainWindow : Window
{
    private readonly CleanerScanService _scanService = new();
    private ScanResult? _lastScan;

    public MainWindow()
    {
        InitializeComponent();

        var userName = Environment.UserName.Trim();
        if (string.IsNullOrEmpty(userName))
        {
            userName = "пользователь";
        }

        WelcomeText.Text = $"Добрый день, {userName}";
        UserInitialText.Text = userName[..1].ToUpperInvariant();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        DetailsButton.IsEnabled = false;
        ScanButton.Content = "Проверяем...";
        StatusText.Text = "Идёт проверка";
        StatusDetails.Text = "Анализируем временные файлы и кэш";

        try
        {
            var result = await _scanService.ScanAsync();
            _lastScan = result;

            StatusText.Text = result.TotalBytes == 0 ? "Всё чисто" : $"Найдено {FormatBytes(result.TotalBytes)}";
            StatusDetails.Text = $"Проверено файлов: {result.TotalFiles:N0}";
            UserTempValue.Text = FormatBytes(result.UserTempBytes);
            UserTempSubtitle.Text = $"{result.UserTempFiles:N0} файлов";
            WindowsTempValue.Text = FormatBytes(result.WindowsTempBytes);
            WindowsTempSubtitle.Text = $"{result.WindowsTempFiles:N0} файлов";
            ActivityText.Text = "Проверка завершена только что";
            ActivityResultText.Text = $"Найдено файлов: {result.TotalFiles:N0}";
            ActivitySizeText.Text = "Выберите категории и подтвердите очистку";
            CleanButton.IsEnabled = result.TotalFiles > 0;
            DetailsButton.IsEnabled = result.TotalFiles > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Проверка не завершена";
            StatusDetails.Text = "Не удалось прочитать часть файлов";
            ActivityText.Text = "Произошла ошибка сканирования";
            ActivityResultText.Text = exception.Message;
            _lastScan = null;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanButton.Content = "Начать проверку";
        }
    }

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScan is null)
        {
            return;
        }

        var details = new StringBuilder();
        details.AppendLine($"Пользовательский Temp: {FormatBytes(_lastScan.UserTempBytes)} ({_lastScan.UserTempFiles.Count:N0} файлов)");
        details.AppendLine($"Системный Temp: {FormatBytes(_lastScan.WindowsTempBytes)} ({_lastScan.WindowsTempFiles.Count:N0} файлов)");
        details.AppendLine();
        details.AppendLine("Примеры найденных файлов:");
        foreach (var file in _lastScan.UserTempFiles.Concat(_lastScan.WindowsTempFiles).Take(8))
        {
            details.AppendLine($"• {file.Path}");
        }

        MessageBox.Show(details.ToString(), "Детали сканирования", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScan is null)
        {
            return;
        }

        var deleteUserTemp = UserTempCheckBox.IsChecked == true;
        var deleteWindowsTemp = WindowsTempCheckBox.IsChecked == true;
        if (!deleteUserTemp && !deleteWindowsTemp)
        {
            MessageBox.Show("Выберите хотя бы одну категорию для очистки.", "Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            "Удалить выбранные временные файлы? Занятые и недоступные файлы будут пропущены.",
            "Подтверждение очистки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        CleanButton.Content = "Очищаем...";
        try
        {
            var deleted = await _scanService.DeleteAsync(_lastScan, deleteUserTemp, deleteWindowsTemp);
            StatusText.Text = deleted == 0 ? "Нечего удалить" : "Очистка завершена";
            StatusDetails.Text = $"Удалено файлов: {deleted:N0}";
            ActivityText.Text = "Очистка выполнена только что";
            ActivityResultText.Text = $"Удалено файлов: {deleted:N0}";
            ActivitySizeText.Text = "Повторите проверку, чтобы увидеть актуальный результат";
            _lastScan = null;
            DetailsButton.IsEnabled = false;
            UserTempValue.Text = "0 Б";
            WindowsTempValue.Text = "0 Б";
            UserTempSubtitle.Text = "Требуется повторная проверка";
            WindowsTempSubtitle.Text = "Требуется повторная проверка";
        }
        finally
        {
            CleanButton.IsEnabled = false;
            CleanButton.Content = "Очистить выбранное";
            ScanButton.IsEnabled = true;
        }
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
}
