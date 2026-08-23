using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class DriveSelectionView : UserControl
{
    private readonly List<CheckBox> _driveChecks = [];
    private readonly Action<IReadOnlyList<string>> _apply;
    private readonly Action _back;
    private readonly AppDialogHost _dialog;

    public DriveSelectionView(IEnumerable<string> selectedDrives, AppDialogHost dialog, Action<IReadOnlyList<string>> apply, Action back)
    {
        InitializeComponent();
        _dialog = dialog;
        _apply = apply;
        _back = back;

        var systemRoot = WindowsDriveService.GetSystemDriveRoot();
        foreach (var drive in DriveInfo.GetDrives().Where(item => item.IsReady && item.DriveType == DriveType.Fixed))
        {
            var isSystemDrive = string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase);
            var check = new CheckBox
            {
                Content = $"{drive.Name}  {drive.VolumeLabel}{(isSystemDrive ? "  · системный диск" : string.Empty)}  · свободно {ByteSizeFormatter.Format(drive.AvailableFreeSpace)} из {ByteSizeFormatter.Format(drive.TotalSize)}",
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

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _driveChecks.Where(check => check.IsChecked == true).Select(check => (string)check.Tag).ToArray();
        if (selected.Length == 0)
        {
            await _dialog.ShowMessageAsync("Cleaner", "Выберите хотя бы один диск.");
            return;
        }

        _apply(selected);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _back();

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
}
