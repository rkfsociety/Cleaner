using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cleaner;

public partial class MainWindow : Window
{
    private readonly CleanerScanService _scanService = new();
    private readonly CleanupHistoryService _historyService = new();
    private readonly CleanerSettingsService _settingsService = new();
    private readonly CleanupPolicyService _policyService = new();
    private readonly DiskUsageService _diskUsageService = new();
    private IReadOnlyList<string> _selectedDriveRoots;
    private int _minimumFileAgeHours;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _cleanupCancellation;
    private CancellationTokenSource? _diskUsageCancellation;
    private System.Windows.Shapes.Path? _diskUsageLoadingPath;
    private ScanResult? _lastScan;
    private Button? _activeNavButton;
    private static readonly TimeSpan MaximumScanAge = TimeSpan.FromMinutes(5);

    public MainWindow()
    {
        InitializeComponent();
        if (DiskLegendPanel.Parent is Grid legendGrid && legendGrid.Parent is Grid cardGrid && cardGrid.ColumnDefinitions.Count > 1)
        {
            cardGrid.ColumnDefinitions[1].Width = new GridLength(390);
            DiskLegendPanel.Width = 215;
        }

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
        Loaded += async (_, _) => await UpdateDiskUsageAsync();
        Unloaded += (_, _) => _diskUsageCancellation?.Cancel();
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
        await UpdateDiskUsageAsync();
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
            var drives = _selectedDriveRoots
                .Select(root => new DriveInfo(root))
                .Where(drive => drive.IsReady && drive.TotalSize > 0)
                .ToArray();
            if (drives.Length == 0)
            {
                return;
            }

            var total = drives.Sum(drive => drive.TotalSize);
            var free = drives.Sum(drive => drive.AvailableFreeSpace);
            var percent = Math.Clamp(free * 100d / total, 0d, 100d);
            FreeSpaceText.Text = $"{percent:0.#}%";
            FreeSpaceProgress.Value = percent;
            RenderDiskUsage(new DiskUsageSnapshot(total,
            [
                new DiskUsageSegment("Прочее", Math.Max(0, total - free), "#A9B0C2"),
                new DiskUsageSegment("Свободно", free, "#B8B1F6")
            ], 0, drives.Select(drive => new DiskUsageDriveSummary(drive.RootDirectory.FullName, drive.TotalSize, drive.AvailableFreeSpace)).ToArray()));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task UpdateDiskUsageAsync()
    {
        _diskUsageCancellation?.Cancel();
        _diskUsageCancellation?.Dispose();
        _diskUsageCancellation = new CancellationTokenSource();
        ShowDiskUsageLoading();
        try
        {
            var snapshot = await _diskUsageService.ReadAsync(_selectedDriveRoots, _diskUsageCancellation.Token);
            if (snapshot is not null && !_diskUsageCancellation.IsCancellationRequested)
            {
                RenderDiskUsage(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowDiskUsageLoading()
    {
        StatusDetails.Text = "Подсчитываем занятое место по выбранным дискам...";
        DiskLegendPanel.Children.Clear();
        DiskLegendPanel.Children.Add(new TextBlock
        {
            Text = "Подробная легенда формируется...",
            Foreground = (Brush)FindResource("Ink"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Width = 205
        });
        DiskLegendPanel.Children.Add(new TextBlock
        {
            Text = $"Выбрано дисков: {_selectedDriveRoots.Count}\nЧитаем каталоги Windows, программ и пользователей",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Width = 205,
            Margin = new Thickness(0, 4, 0, 0)
        });

        _diskUsageLoadingPath = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C5CE7")),
            StrokeThickness = 12,
            Data = CreateArc(72.5, 66.5, -90, 70),
            RenderTransform = new RotateTransform { CenterX = 72.5, CenterY = 72.5 }
        };
        DiskUsageRing.Children.Add(_diskUsageLoadingPath);
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        ((RotateTransform)_diskUsageLoadingPath.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void RenderDiskUsage(DiskUsageSnapshot snapshot)
    {
        var free = snapshot.Segments.FirstOrDefault(segment => segment.Name == "Свободно")?.Bytes ?? 0;
        var percent = snapshot.TotalBytes <= 0 ? 0 : Math.Clamp(free * 100d / snapshot.TotalBytes, 0d, 100d);
        FreeSpaceText.Text = $"{percent:0.#}%";
        FreeSpaceProgress.Value = percent;
        if (snapshot.SkippedDrives > 0)
        {
            StatusDetails.Text = $"Доступные данные по дискам · не удалось прочитать: {snapshot.SkippedDrives}";
        }
        else if (snapshot.FromCache)
        {
            StatusDetails.Text = "Данные использования диска взяты из кэша · пересчёт не потребовался";
        }

        DiskUsageRing.Children.Clear();
        DiskUsageRing.Children.Add(new System.Windows.Shapes.Ellipse { Width = 145, Height = 145, Stroke = (Brush)FindResource("Line"), StrokeThickness = 12 });
        const double center = 72.5;
        const double radius = 66.5;
        var angle = -90d;
        foreach (var segment in snapshot.Segments.Where(segment => segment.Bytes > 0))
        {
            var sweep = segment.Bytes * 360d / snapshot.TotalBytes;
            var visibleSweep = Math.Max(0.5, sweep - 1.5);
            var path = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(segment.Color)),
                StrokeThickness = 12,
                Data = CreateArc(center, radius, angle, visibleSweep)
            };
            DiskUsageRing.Children.Add(path);
            angle += sweep;
        }

        DiskLegendPanel.Children.Clear();
        foreach (var segment in snapshot.Segments.Where(segment => segment.Bytes > 0))
        {
            var share = segment.Bytes * 100d / snapshot.TotalBytes;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(segment.Color)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{segment.Name}: {ByteSizeFormatter.Format(segment.Bytes)} ({share:0.#}%)", Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Width = 122 });
            DiskLegendPanel.Children.Add(row);
        }

        var drives = snapshot.Drives ?? [];
        if (drives.Count > 0)
        {
            DiskLegendPanel.Children.Add(new TextBlock { Text = $"Выбрано дисков: {drives.Count}", Foreground = (Brush)FindResource("Ink"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 1) });
            foreach (var drive in drives)
            {
                var root = drive.Root.TrimEnd('\\');
                var share = drive.TotalBytes <= 0 ? 0 : drive.FreeBytes * 100d / drive.TotalBytes;
                DiskLegendPanel.Children.Add(new TextBlock
                {
                    Text = $"{root}: {ByteSizeFormatter.Format(drive.TotalBytes)} · свободно {ByteSizeFormatter.Format(drive.FreeBytes)} ({share:0.#}%)",
                    Foreground = (Brush)FindResource("Muted"),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Width = 205
                });
            }
        }
    }

    private static StreamGeometry CreateArc(double center, double radius, double startAngle, double sweepAngle)
    {
        static Point PointAt(double center, double radius, double angle)
        {
            var radians = angle * Math.PI / 180d;
            return new Point(center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointAt(center, radius, startAngle), false, false);
            context.ArcTo(PointAt(center, radius, startAngle + sweepAngle), new Size(radius, radius), 0, sweepAngle >= 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
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
