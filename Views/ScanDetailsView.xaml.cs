using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class ScanDetailsView : UserControl
{
    private readonly Action _back;

    public ScanDetailsView(ScanResult scan, Action back)
    {
        InitializeComponent();
        _back = back;
        SummaryText.Text = $"Проверка: {scan.ScannedAt.LocalDateTime:g}. Корзина: {scan.RecycleBin.Items:N0} объектов, {ByteSizeFormatter.Format(scan.RecycleBin.Bytes)}. Ниже перечислены все найденные файлы Temp и кэшей.";
        FilesList.ItemsSource = scan.UserTempFiles
            .Select(file => new ScanFileRow("Temp и кэш пользователя", ByteSizeFormatter.Format(file.Bytes), file.Path))
            .Concat(scan.WindowsTempFiles.Select(file => new ScanFileRow("Системный Temp", ByteSizeFormatter.Format(file.Bytes), file.Path)))
            .ToArray();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _back();

    private sealed record ScanFileRow(string Category, string Size, string Path);
}
