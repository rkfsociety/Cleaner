using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace Cleaner;

public partial class MainWindow : Window
{
    private readonly CleanerScanService _scanService = new();
    private readonly CleanupHistoryService _historyService = new();
    private readonly CleanerSettingsService _settingsService = new();
    private IReadOnlyList<string> _selectedDriveRoots;
    private CancellationTokenSource? _scanCancellation;
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
        _selectedDriveRoots = _settingsService.LoadSelectedDrives(GetDefaultDriveRoots());
        DriveButton.Content = $"Диски: {_selectedDriveRoots.Count}";
        UpdateFreeSpaceIndicator();
        RestoreLatestActivity();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null)
        {
            _scanCancellation.Cancel();
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        DetailsButton.IsEnabled = false;
        ScanButton.Content = "Отменить";
        ScanButton.IsEnabled = true;
        StatusText.Text = "Идёт проверка";
        StatusDetails.Text = "Анализируем временные файлы и кэш";

        try
        {
            var result = await _scanService.ScanAsync(_selectedDriveRoots, _scanCancellation.Token);
            _lastScan = result;

            StatusText.Text = result.TotalBytes == 0 ? "Всё чисто" : $"Найдено {FormatBytes(result.TotalBytes)}";
            StatusDetails.Text = $"Проверено файлов: {result.TotalFiles:N0}";
            UserTempValue.Text = FormatBytes(result.UserTempBytes);
            UserTempSubtitle.Text = $"{result.UserTempFiles.Count:N0} файлов";
            WindowsTempValue.Text = FormatBytes(result.WindowsTempBytes);
            WindowsTempSubtitle.Text = $"{result.WindowsTempFiles.Count:N0} файлов";
            RecycleBinValue.Text = FormatBytes(result.RecycleBin.Bytes);
            RecycleBinSubtitle.Text = $"{result.RecycleBin.Items:N0} объектов";
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
        catch (OperationCanceledException)
        {
            StatusText.Text = "Проверка отменена";
            StatusDetails.Text = "Можно запустить её снова в любой момент";
            ActivityText.Text = "Проверка отменена пользователем";
            ActivityResultText.Text = "Данные не удалялись";
            ActivitySizeText.Text = "Выберите категории после новой проверки";
            _lastScan = null;
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
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
        details.AppendLine($"Корзина: {FormatBytes(_lastScan.RecycleBin.Bytes)} ({_lastScan.RecycleBin.Items:N0} объектов)");
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
        var deleteRecycleBin = RecycleBinCheckBox.IsChecked == true;
        if (!deleteUserTemp && !deleteWindowsTemp && !deleteRecycleBin)
        {
            MessageBox.Show("Выберите хотя бы одну категорию для очистки.", "Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedBytes = (deleteUserTemp ? _lastScan.UserTempBytes : 0) +
            (deleteWindowsTemp ? _lastScan.WindowsTempBytes : 0) +
            (deleteRecycleBin ? _lastScan.RecycleBin.Bytes : 0);
        var runningBrowsers = new[] { "chrome", "msedge", "firefox" }
            .Where(name => Process.GetProcessesByName(name).Length > 0)
            .ToArray();
        var browserWarning = runningBrowsers.Length > 0
            ? $"\n\nЗапущены: {string.Join(", ", runningBrowsers)}. Их занятые файлы кэша будут пропущены."
            : string.Empty;

        var confirmation = MessageBox.Show(
            $"Удалить выбранные данные (до {FormatBytes(selectedBytes)})? Занятые и недоступные файлы будут пропущены.{browserWarning}",
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
            var cleanup = await _scanService.DeleteAsync(_lastScan, _selectedDriveRoots, deleteUserTemp, deleteWindowsTemp, deleteRecycleBin);
            var scopes = new List<string>();
            if (deleteUserTemp) scopes.Add("Пользовательский Temp");
            if (deleteWindowsTemp) scopes.Add("Системный Temp");
            if (deleteRecycleBin) scopes.Add("Корзина");
            var scope = string.Join(", ", scopes);
            _historyService.Append(new CleanupHistoryEntry(DateTimeOffset.Now, scope, cleanup.DeletedFiles, cleanup.ReclaimedBytes));
            StatusText.Text = cleanup.DeletedFiles == 0 ? "Нечего удалить" : "Очистка завершена";
            StatusDetails.Text = $"Удалено: {cleanup.DeletedFiles:N0}, пропущено: {cleanup.SkippedFiles:N0}";
            ActivityText.Text = "Очистка выполнена только что";
            ActivityResultText.Text = $"Удалено файлов: {cleanup.DeletedFiles:N0}";
            ActivitySizeText.Text = $"Освобождено {FormatBytes(cleanup.ReclaimedBytes)} · пропущено: {cleanup.SkippedFiles:N0}";
            _lastScan = null;
            DetailsButton.IsEnabled = false;
            UserTempValue.Text = "—";
            WindowsTempValue.Text = "—";
            RecycleBinValue.Text = "—";
            UserTempSubtitle.Text = "Требуется повторная проверка";
            WindowsTempSubtitle.Text = "Требуется повторная проверка";
            RecycleBinSubtitle.Text = "Требуется повторная проверка";
            UpdateFreeSpaceIndicator();
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

    private void DriveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DriveSelectionWindow(_selectedDriveRoots) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _selectedDriveRoots = dialog.SelectedDrives;
            _settingsService.SaveSelectedDrives(_selectedDriveRoots);
            DriveButton.Content = $"Диски: {_selectedDriveRoots.Count}";
            _lastScan = null;
            CleanButton.IsEnabled = false;
            DetailsButton.IsEnabled = false;
            StatusText.Text = "Диски выбраны";
            StatusDetails.Text = "Запустите проверку заново";
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HistoryWindow(_historyService.LoadAll()) { Owner = this };
        dialog.ShowDialog();
    }

    private void CleanupNavigation_Click(object sender, RoutedEventArgs e)
    {
        ScanButton_Click(sender, e);
    }

    private void SettingsNavigation_Click(object sender, RoutedEventArgs e)
    {
        var isAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleaner", "history.json");
        MessageBox.Show(
            $"Режим администратора: {(isAdministrator ? "включён" : "не включён")}\nВыбрано дисков: {_selectedDriveRoots.Count}\nФайл журнала: {historyPath}",
            "Настройки Cleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void HelpNavigation_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "1. Нажмите «Начать проверку».\n2. При необходимости выберите диски.\n3. Отметьте категории очистки.\n4. Откройте детали и нажмите «Очистить выбранное».\n5. Подтвердите удаление.\n\nЗанятые и недоступные файлы пропускаются.",
            "Как пользоваться Cleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static IReadOnlyList<string> GetDefaultDriveRoots()
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.RootDirectory.FullName)
            .ToArray();
        return drives.Length > 0 ? drives : [Path.GetPathRoot(Environment.SystemDirectory)!];
    }

    private void UpdateFreeSpaceIndicator()
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                return;
            }

            var drive = new DriveInfo(systemRoot);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return;
            }

            var percent = Math.Clamp(drive.AvailableFreeSpace * 100d / drive.TotalSize, 0d, 100d);
            FreeSpaceText.Text = $"{percent:0.#}%";
            FreeSpaceProgress.Value = percent;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RestoreLatestActivity()
    {
        var latest = _historyService.LoadLatest();
        if (latest is null)
        {
            return;
        }

        ActivityText.Text = $"Последняя очистка: {latest.Timestamp.LocalDateTime:g}";
        ActivityResultText.Text = $"Удалено файлов: {latest.DeletedFiles:N0}";
        ActivitySizeText.Text = $"{latest.Scope} · освобождено {FormatBytes(latest.ReclaimedBytes)}";
    }
}
