using System.Windows;

namespace Cleaner;

public partial class ScanDetailsWindow : Window
{
    public ScanDetailsWindow(ScanResult scan)
    {
        InitializeComponent();
        SummaryText.Text = $"Проверка: {scan.ScannedAt.LocalDateTime:g}. Корзина: {scan.RecycleBin.Items:N0} объектов, {ByteSizeFormatter.Format(scan.RecycleBin.Bytes)}. Ниже перечислены все найденные файлы Temp и кэшей.";
        FilesList.ItemsSource = scan.UserTempFiles
            .Select(file => new ScanFileRow("Temp и кэш пользователя", ByteSizeFormatter.Format(file.Bytes), file.Path))
            .Concat(scan.WindowsTempFiles.Select(file => new ScanFileRow("Системный Temp", ByteSizeFormatter.Format(file.Bytes), file.Path)))
            .ToArray();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ScanFileRow(string Category, string Size, string Path);
}
