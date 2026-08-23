using System.Windows;

namespace Cleaner;

public partial class ScanDetailsWindow : Window
{
    public ScanDetailsWindow(ScanResult scan)
    {
        InitializeComponent();
        SummaryText.Text = $"Проверка: {scan.ScannedAt.LocalDateTime:g}. Корзина: {scan.RecycleBin.Items:N0} объектов, {FormatBytes(scan.RecycleBin.Bytes)}. Ниже перечислены все найденные файлы Temp и кэшей.";
        FilesList.ItemsSource = scan.UserTempFiles
            .Select(file => new ScanFileRow("Temp и кэш пользователя", FormatBytes(file.Bytes), file.Path))
            .Concat(scan.WindowsTempFiles.Select(file => new ScanFileRow("Системный Temp", FormatBytes(file.Bytes), file.Path)))
            .ToArray();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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

    private sealed record ScanFileRow(string Category, string Size, string Path);
}
