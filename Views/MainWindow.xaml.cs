using System.ComponentModel;
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
    private readonly UpdateInstallerService _updateInstallerService = new();
    private IReadOnlyList<string> _selectedDriveRoots;
    private int _minimumFileAgeHours;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _cleanupCancellation;
    private CancellationTokenSource? _diskUsageCancellation;
    private System.Windows.Shapes.Path? _diskUsageLoadingPath;
    private ScanResult? _lastScan;
    private readonly HashSet<string> _excludedScanPaths = new(StringComparer.OrdinalIgnoreCase);
    private ProgramsView? _programsView;
    private Button? _activeNavButton;
    private static readonly TimeSpan MaximumScanAge = TimeSpan.FromMinutes(5);

    public MainWindow()
    {
        InitializeComponent();
        TitleVersionText.Text = $"v{CleanerVersion.Current}";

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
        Loaded += async (_, _) =>
        {
            var updateFailure = _updateInstallerService.ConsumeInstallFailure();
            if (updateFailure is not null)
            {
                await Dialog.ShowMessageAsync("Обновление Cleaner", updateFailure);
            }
            await UpdateDiskUsageAsync();
        };
        Unloaded += (_, _) => _diskUsageCancellation?.Cancel();
        RestoreLatestActivity();
        ShowHome();
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableDarkFrame();
    }

    private void EnableDarkFrame()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int trueVal = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 11 / Windows 10 (18985+), 19 on older Windows 10
            if (DwmSetWindowAttribute(hwnd, 20, ref trueVal, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref trueVal, sizeof(int));
            }
        }
        catch
        {
            // dwmapi is unavailable on non-Windows or legacy environments
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowFrameBorder.BorderThickness = new Thickness(0);
            WindowFrameBorder.Margin = new Thickness(7);
            MaximizeButtonIcon.Data = Geometry.Parse("M 3,1 L 10,1 L 10,8 L 8,8 L 8,10 L 1,10 L 1,3 L 3,3 Z M 3,3 L 8,3 L 8,8 L 3,8 Z");
        }
        else
        {
            WindowFrameBorder.BorderThickness = new Thickness(1);
            WindowFrameBorder.Margin = new Thickness(0);
            MaximizeButtonIcon.Data = Geometry.Parse("M 1,1 L 9,1 L 9,9 L 1,9 Z");
        }
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
            _excludedScanPaths.Clear();

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

        ShowPage(new ScanDetailsView(_lastScan, _excludedScanPaths, ShowHome), null);
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

        var userSelectedBytes = deleteUserTemp
            ? _lastScan.UserTempFiles.Where(file => !_excludedScanPaths.Contains(file.Path)).Sum(file => file.Bytes)
            : 0;
        var windowsSelectedBytes = deleteWindowsTemp
            ? _lastScan.WindowsTempFiles.Where(file => !_excludedScanPaths.Contains(file.Path)).Sum(file => file.Bytes)
            : 0;
        var selectedBytes = userSelectedBytes +
            windowsSelectedBytes +
            (deleteRecycleBin ? _lastScan.RecycleBin.Bytes : 0);
        var runningBrowsers = GetRunningBrowserNames();
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
            var cleanup = await _scanService.DeleteAsync(_lastScan, _selectedDriveRoots, deleteUserTemp, deleteWindowsTemp, deleteRecycleBin, _minimumFileAgeHours, _cleanupCancellation.Token, progress, _excludedScanPaths);
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
            _excludedScanPaths.Clear();
            DetailsButton.IsEnabled = false;
            UserTempValue.Text = "—";
            WindowsTempValue.Text = "—";
            RecycleBinValue.Text = "—";
            UserTempSubtitle.Text = "Требуется повторная проверка";
            WindowsTempSubtitle.Text = "Требуется повторная проверка";
            RecycleBinSubtitle.Text = "Требуется повторная проверка";
            await UpdateDiskUsageAsync(forceRefresh: true);
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
        _programsView ??= new ProgramsView(Dialog);
        ShowPage(_programsView, ProgramsNavButton);
    }

    private void HistoryNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(new HistoryView(_historyService, Dialog), HistoryNavButton);
    }

    private void SettingsNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(new SettingsView(_minimumFileAgeHours, _selectedDriveRoots.Count, ApplyMinimumAge, ShowDriveSelection, Dialog), SettingsNavButton);
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

    private async Task UpdateDiskUsageAsync(bool forceRefresh = false)
    {
        _diskUsageCancellation?.Cancel();
        _diskUsageCancellation?.Dispose();
        _diskUsageCancellation = new CancellationTokenSource();
        ShowDiskUsageLoading();
        try
        {
            var snapshot = await _diskUsageService.ReadAsync(_selectedDriveRoots, _diskUsageCancellation.Token, forceRefresh);
            if (snapshot is not null && !_diskUsageCancellation.IsCancellationRequested)
            {
                RenderDiskUsage(snapshot, updateStatusText: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowDiskUsageLoading()
    {
        if (_lastScan is null && _scanCancellation is null && _cleanupCancellation is null)
        {
            StatusDetails.Text = "Оцениваем занятое место по выбранным дискам...";
        }

        DiskLegendPanel.Children.Clear();
        DiskLegendPanel.Children.Add(new TextBlock
        {
            Text = "Подробная легенда формируется...",
            Foreground = (Brush)FindResource("Ink"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        DiskLegendPanel.Children.Add(new TextBlock
        {
            Text = $"Выбрано дисков: {_selectedDriveRoots.Count}\nЧитаем каталоги Windows, программ и пользователей",
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
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

    private void RenderDiskUsage(DiskUsageSnapshot snapshot, bool updateStatusText = false)
    {
        var free = snapshot.Segments.FirstOrDefault(segment => segment.Name == "Свободно")?.Bytes ?? 0;
        var percent = snapshot.TotalBytes <= 0 ? 0 : Math.Clamp(free * 100d / snapshot.TotalBytes, 0d, 100d);
        FreeSpaceText.Text = $"{percent:0.#}%";
        FreeSpaceProgress.Value = percent;
        SidebarDriveSubtext.Text = $"Свободно {ByteSizeFormatter.Format(free)} из {ByteSizeFormatter.Format(snapshot.TotalBytes)}";

        if (updateStatusText && _lastScan is null && _scanCancellation is null && _cleanupCancellation is null)
        {
            if (snapshot.SkippedDrives > 0)
            {
                StatusDetails.Text = $"Доступные данные по дискам · не удалось прочитать: {snapshot.SkippedDrives}";
            }
            else
            {
                StatusDetails.Text = "Готов к проверке временных файлов, кэшей и корзины";
            }
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
            var row = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(segment.Color)),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = new TextBlock
            {
                Text = $"{segment.Name}: {ByteSizeFormatter.Format(segment.Bytes)} ({share:0.#}%)",
                Foreground = (Brush)FindResource("Ink"),
                Opacity = 0.88,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(label, 1);
            row.Children.Add(dot);
            row.Children.Add(label);
            DiskLegendPanel.Children.Add(row);
        }

        var drives = snapshot.Drives ?? [];
        if (drives.Count > 0)
        {
            DiskLegendPanel.Children.Add(new Border
            {
                Height = 1,
                Background = (Brush)FindResource("Line"),
                Margin = new Thickness(0, 6, 0, 5)
            });
            foreach (var drive in drives.Take(2))
            {
                var root = drive.Root.TrimEnd('\\');
                var share = drive.TotalBytes <= 0 ? 0 : drive.FreeBytes * 100d / drive.TotalBytes;
                DiskLegendPanel.Children.Add(new TextBlock
                {
                    Text = $"Диск {root} · свободно {ByteSizeFormatter.Format(drive.FreeBytes)} из {ByteSizeFormatter.Format(drive.TotalBytes)} ({share:0.#}%)",
                    Foreground = (Brush)FindResource("Muted"),
                    FontSize = 10.5,
                    TextTrimming = TextTrimming.CharacterEllipsis
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

    private static string[] GetRunningBrowserNames()
    {
        var running = new List<string>();
        foreach (var name in new[] { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    running.Add(name);
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        var browserProcesses = Process.GetProcessesByName("browser");
        try
        {
            foreach (var process in browserProcesses)
            {
                try
                {
                    if (IsYandexBrowserExecutablePath(process.MainModule?.FileName))
                    {
                        running.Add("YandexBrowser");
                        break;
                    }
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
            }
        }
        finally
        {
            foreach (var process in browserProcesses)
            {
                process.Dispose();
            }
        }

        return running.ToArray();
    }

    internal static bool IsYandexBrowserExecutablePath(string? executablePath)
    {
        return !string.IsNullOrWhiteSpace(executablePath) &&
            executablePath.Contains(@"\Yandex\YandexBrowser\", StringComparison.OrdinalIgnoreCase);
    }
}
