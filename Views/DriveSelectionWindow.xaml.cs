using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class DriveSelectionWindow : Window
{
    private readonly List<CheckBox> _driveChecks = [];
    public IReadOnlyList<string> SelectedDrives { get; private set; } = [];

    public DriveSelectionWindow(IEnumerable<string> selectedDrives)
    {
        InitializeComponent();

        var systemRoot = WindowsDriveService.GetSystemDriveRoot();
        foreach (var drive in DriveInfo.GetDrives().Where(item => item.IsReady && item.DriveType == DriveType.Fixed))
        {
            var isSystemDrive = string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase);
            var check = new CheckBox
            {
                Content = $"{drive.Name}  {drive.VolumeLabel}{(isSystemDrive ? "  · системный диск" : string.Empty)}  · свободно {FormatBytes(drive.AvailableFreeSpace)} из {FormatBytes(drive.TotalSize)}",
                Tag = drive.RootDirectory.FullName,
                IsChecked = selectedDrives.Contains(drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase),
                FontSize = 14,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3)
            };
            _driveChecks.Add(check);
            DrivePanel.Children.Add(check);
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedDrives = _driveChecks.Where(check => check.IsChecked == true).Select(check => (string)check.Tag).ToArray();
        if (SelectedDrives.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы один диск.", "Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var check in _driveChecks)
        {
            check.IsChecked = true;
        }
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var check in _driveChecks)
        {
            check.IsChecked = false;
        }
    }

    private static string FormatBytes(long bytes) => ByteSizeFormatter.Format(bytes);
}
