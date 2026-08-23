using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class SettingsView : UserControl
{
    private readonly Action<int> _apply;
    private readonly Action _openDrives;

    public SettingsView(int minimumFileAgeHours, int selectedDriveCount, Action<int> apply, Action openDrives)
    {
        InitializeComponent();
        _apply = apply;
        _openDrives = openDrives;
        AgeFilterCheckBox.IsChecked = minimumFileAgeHours > 0;
        AdminStatusText.Text = $"Права администратора: {(App.IsAdministrator ? "включены" : "не включены")}";
        DriveStatusText.Text = $"Выбрано дисков: {selectedDriveCount}; системный диск: {WindowsDriveService.GetSystemDriveRoot().TrimEnd('\\')}";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _apply(AgeFilterCheckBox.IsChecked == true ? 24 : 0);
        SaveStatusText.Text = $"Сохранено в {DateTime.Now:HH:mm}";
    }

    private void DrivesButton_Click(object sender, RoutedEventArgs e) => _openDrives();
}
