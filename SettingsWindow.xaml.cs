using System.Security.Principal;
using System.Windows;

namespace Cleaner;

public partial class SettingsWindow : Window
{
    public int MinimumFileAgeHours { get; private set; }

    public SettingsWindow(int minimumFileAgeHours, int selectedDriveCount)
    {
        InitializeComponent();
        MinimumFileAgeHours = minimumFileAgeHours;
        AgeFilterCheckBox.IsChecked = minimumFileAgeHours > 0;
        var isAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        AdminStatusText.Text = $"Права администратора: {(isAdministrator ? "включены" : "не включены")}";
        DriveStatusText.Text = $"Выбрано дисков: {selectedDriveCount}";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        MinimumFileAgeHours = AgeFilterCheckBox.IsChecked == true ? 24 : 0;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
