using System.Windows;

namespace Cleaner;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanButton.Content = "Проверяем...";
        StatusText.Text = "Идёт проверка";
        StatusDetails.Text = "Анализируем временные файлы и кэш";

        await Task.Delay(900);

        ScanButton.IsEnabled = true;
        ScanButton.Content = "Начать проверку";
        StatusText.Text = "Всё чисто";
        StatusDetails.Text = "Проверка завершена только что";
    }
}
