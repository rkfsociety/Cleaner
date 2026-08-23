using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cleaner;

public partial class MainWindow : Window
{
    private readonly CleanerScanService _scanService = new();
    private readonly CleanupHistoryService _historyService = new();
    private readonly CleanerSettingsService _settingsService = new();
    private readonly CleanupPolicyService _policyService = new();
    private IReadOnlyList<string> _selectedDriveRoots;
    private int _minimumFileAgeHours;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _cleanupCancellation;
    private ScanResult? _lastScan;
    private Button? _activeNavButton;
    private static readonly TimeSpan MaximumScanAge = TimeSpan.FromMinutes(5);

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
        _selectedDriveRoots = _settingsService.LoadSelectedDrives(GetDefaultDriveRoots(), WindowsDriveService.GetSystemDriveRoot());
        _minimumFileAgeHours = _policyService.LoadMinimumAgeHours();
        UpdateDriveButtonCaption();
        UpdateFreeSpaceIndicator();
        RestoreLatestActivity();
        ShowHome();
        if (App.IsSystemCleanupSession)
        {
            UserTempCheckBox.IsChecked = false;
            UserTempCheckBox.IsEnabled = false;
            RecycleBinCheckBox.IsChecked = false;
            RecycleBinCheckBox.IsEnabled = false;
            StatusText.Text = "Системная очистка";
            StatusDetails.Text = "Открыта повышенная сессия только для системного Temp";
        }
    }

    /// <summary>Единственное окно приложения: все разделы и диалоги показываются внутри него.</summary>
    public AppDialogHost DialogHost => Dialog;

    private void ShowHome()
    {
        PageHost.Content = null;
        PageHost.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        HighlightNavigation(HomeNavButton);
    }

    private void ShowPage(UIElement page, Button? navButton)
    {
        PageHost.Content = page;
        PageHost.Visibility = Visibility.Visible;
        HomeView.Visibility = Visibility.Collapsed;
        HighlightNavigation(navButton);
    }

    private void HighlightNavigation(Button? navButton)
    {
        if (_activeNavButton is not null)
        {
            _activeNavButton.ClearValue(BackgroundProperty);
            _activeNavButton.ClearValue(ForegroundProperty);
        }

        _activeNavButton = navButton;
        if (navButton is null)
        {
            return;
        }

        navButton.Background = (Brush)FindResource("PurpleLight");
        navButton.Foreground = (Brush)FindResource("Purple");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = Dialog.HandleKey(e.Key);
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
        ScanProgressBar.Visibility = Visibility.Visible;
        var progress = new Progress<ScanProgress>(update =>
        {
            StatusDetails.Text = $"{update.Stage}: найдено {update.FilesFound:N0} файлов ({FormatBytes(update.BytesFound)})";
        });

        try
        {
            var result = await _scanService.ScanAsync(_selectedDriveRoots, _scanCancellation.Token, progress);
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
            ScanProgressBar.Visibility = Visibility.Collapsed;
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

        ShowPage(new ScanDetailsView(_lastScan, ShowHome), null);
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupCancellation is not null)
        {
            _cleanupCancellation.Cancel();
            CleanButton.IsEnabled = false;
            CleanButton.Content = "Отмена...";
            return;
        }

        if (_lastScan is null)
        {
            return;
        }

        if (!_lastScan.IsFresh(MaximumScanAge, DateTimeOffset.Now))
        {
            InvalidateScan("Результат устарел", "Для безопасной очистки запустите новую проверку.");
            await Dialog.ShowMessageAsync("Нужна новая проверка", "Результат проверки старше пяти минут. Чтобы не удалить данные, появившиеся позже, выполните новую проверку.");
            return;
        }

        var deleteUserTemp = UserTempCheckBox.IsChecked == true;
        var deleteWindowsTemp = WindowsTempCheckBox.IsChecked == true;
        var deleteRecycleBin = RecycleBinCheckBox.IsChecked == true;
        if (!deleteUserTemp && !deleteWindowsTemp && !deleteRecycleBin)
        {
            await Dialog.ShowMessageAsync("Cleaner", "Выберите хотя бы одну категорию для очистки.");
            return;
        }

        if (deleteWindowsTemp && !App.IsAdministrator)
        {
            var elevation = await Dialog.ConfirmAsync(
                "Нужны права администратора",
                "Для системного Temp нужна отдельная повышенная сессия. Cleaner перезапустится в режиме только системной очистки; после запуска повторите проверку.",
                "Перезапустить");
            if (elevation && !App.RestartForSystemCleanup())
            {
                await Dialog.ShowMessageAsync("Cleaner", "Не удалось получить права администратора. Системные файлы останутся без изменений.");
            }

            return;
        }

        if (deleteRecycleBin)
        {
            var currentRecycleBin = await Task.Run(() => _scanService.GetRecycleBinInfo(_selectedDriveRoots));
            if (CleanerScanService.HasRecycleBinChanged(_lastScan.RecycleBin, currentRecycleBin))
            {
                InvalidateScan("Корзина изменилась", "Для безопасной очистки запустите новую проверку.");
                await Dialog.ShowMessageAsync("Нужна новая проверка", "Содержимое корзины изменилось после проверки. Чтобы не удалить новые данные без отдельного подтверждения, выполните новую проверку.");
                return;
            }
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
        var ageWarning = _minimumFileAgeHours > 0
            ? $"\nФайлы младше {_minimumFileAgeHours} часов будут пропущены."
            : string.Empty;

        var confirmation = await Dialog.ConfirmAsync(
            "Подтверждение очистки",
            $"Удалить выбранные данные (до {FormatBytes(selectedBytes)})? Занятые и недоступные файлы будут пропущены.{ageWarning}{browserWarning}",
            "Очистить");
        if (!confirmation)
        {
            return;
        }

        _cleanupCancellation = new CancellationTokenSource();
        CleanButton.IsEnabled = true;
        ScanButton.IsEnabled = false;
        CleanButton.Content = "Отменить очистку";
        ScanProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Идёт очистка";
        StatusDetails.Text = "Удаляем только файлы из только что проверенного списка";
        var progress = new Progress<CleanupProgress>(update =>
        {
            StatusDetails.Text = $"{update.Stage}: удалено {update.DeletedFiles:N0}, пропущено {update.SkippedFiles:N0} ({FormatBytes(update.ReclaimedBytes)})";
        });
        try
        {
            var cleanup = await _scanService.DeleteAsync(_lastScan, _selectedDriveRoots, deleteUserTemp, deleteWindowsTemp, deleteRecycleBin, _minimumFileAgeHours, _cleanupCancellation.Token, progress);
            var scopes = new List<string>();
            if (deleteUserTemp) scopes.Add("Пользовательский Temp");
            if (deleteWindowsTemp) scopes.Add("Системный Temp");
            if (deleteRecycleBin) scopes.Add("Корзина");
            var scope = string.Join(", ", scopes);
            var historySaved = _historyService.Append(new CleanupHistoryEntry(DateTimeOffset.Now, scope, cleanup.DeletedFiles, cleanup.ReclaimedBytes));
            StatusText.Text = cleanup.DeletedFiles == 0 ? "Нечего удалить" : "Очистка завершена";
            StatusDetails.Text = $"Удалено: {cleanup.DeletedFiles:N0}, пропущено: {cleanup.SkippedFiles:N0}";
            ActivityText.Text = "Очистка выполнена только что";
            ActivityResultText.Text = $"Удалено файлов: {cleanup.DeletedFiles:N0}";
            ActivitySizeText.Text = $"Освобождено {FormatBytes(cleanup.ReclaimedBytes)} · пропущено: {cleanup.SkippedFiles:N0}" + (historySaved ? string.Empty : ". История не сохранена");
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
        catch (OperationCanceledException)
        {
            StatusText.Text = "Очистка отменена";
            StatusDetails.Text = "Уже удалённые файлы не восстанавливаются; выполните новую проверку для точных данных.";
            ActivityText.Text = "Очистка отменена пользователем";
            ActivityResultText.Text = "Данные удалялись только до момента отмены";
            _lastScan = null;
            DetailsButton.IsEnabled = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogService.Write("Ошибка очистки", exception);
            StatusText.Text = "Очистка завершилась с ошибкой";
            StatusDetails.Text = "Часть данных могла быть удалена. Выполните новую проверку.";
            _lastScan = null;
            DetailsButton.IsEnabled = false;
        }
        finally
        {
            _cleanupCancellation.Dispose();
            _cleanupCancellation = null;
            CleanButton.IsEnabled = false;
            CleanButton.Content = "Очистить выбранное";
            ScanProgressBar.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
        }
    }

    private void InvalidateScan(string status, string details)
    {
        _lastScan = null;
        CleanButton.IsEnabled = false;
        DetailsButton.IsEnabled = false;
        StatusText.Text = status;
        StatusDetails.Text = details;
    }

    private static string FormatBytes(long bytes) => ByteSizeFormatter.Format(bytes);

    private void DriveButton_Click(object sender, RoutedEventArgs e) => ShowDriveSelection();

    private void ShowDriveSelection()
    {
        ShowPage(new DriveSelectionView(_selectedDriveRoots, Dialog, ApplySelectedDrives, ShowHome), null);
    }

    private async void ApplySelectedDrives(IReadOnlyList<string> drives)
    {
        _selectedDriveRoots = drives;
        UpdateDriveButtonCaption();
        _lastScan = null;
        CleanButton.IsEnabled = false;
        DetailsButton.IsEnabled = false;
        StatusText.Text = "Диски выбраны";
        StatusDetails.Text = "Запустите проверку заново";
        ShowHome();
        if (!_settingsService.SaveSelectedDrives(_selectedDriveRoots))
        {
            await Dialog.ShowMessageAsync("Cleaner", "Выбор дисков применён для текущего запуска, но не сохранён. Проверьте доступ к папке данных Cleaner.");
        }
    }

    private void UpdateDriveButtonCaption()
    {
        DriveButton.Content = $"Диски: {_selectedDriveRoots.Count} · системный {WindowsDriveService.GetSystemDriveRoot().TrimEnd('\\')}";
    }

    private void HomeNavigation_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void ProgramsNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(new ProgramsView(Dialog), ProgramsNavButton);
    }

    private void HistoryNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(new HistoryView(_historyService, Dialog), HistoryNavButton);
    }

    private void SettingsNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(new SettingsView(_minimumFileAgeHours, _selectedDriveRoots.Count, ApplyMinimumAge, ShowDriveSelection), SettingsNavButton);
    }

    private async void ApplyMinimumAge(int minimumFileAgeHours)
    {
        _minimumFileAgeHours = minimumFileAgeHours;
        if (!_policyService.SaveMinimumAgeHours(_minimumFileAgeHours))
        {
            await Dialog.ShowMessageAsync("Cleaner", "Режим применён для текущего запуска, но не сохранён. Проверьте доступ к папке данных Cleaner.");
        }
    }

    private void HelpNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(new HelpView(), HelpNavButton);
    }

    private static IReadOnlyList<string> GetDefaultDriveRoots()
    {
        return WindowsDriveService.GetFixedDriveRoots();
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
