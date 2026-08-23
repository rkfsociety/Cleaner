using System.Windows;

namespace Cleaner;

public partial class MainWindow : Window
{
    private readonly CleanerScanService _scanService = new();

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
        ScanButton.Content = "Проверяем...";
        StatusText.Text = "Идёт проверка";
        StatusDetails.Text = "Анализируем временные файлы и кэш";

        var result = await _scanService.ScanAsync();

        ScanButton.IsEnabled = true;
        ScanButton.Content = "Начать проверку";
        StatusText.Text = result.TotalBytes == 0 ? "Всё чисто" : $"Найдено {FormatBytes(result.TotalBytes)}";
        StatusDetails.Text = $"Проверено файлов: {result.TotalFiles:N0}";
        UserTempValue.Text = FormatBytes(result.UserTempBytes);
        UserTempSubtitle.Text = $"{result.UserTempFiles:N0} файлов";
        WindowsTempValue.Text = FormatBytes(result.WindowsTempBytes);
        WindowsTempSubtitle.Text = $"{result.WindowsTempFiles:N0} файлов";
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
