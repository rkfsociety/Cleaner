using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Cleaner;

public partial class ProgramsView : UserControl
{
    private readonly InstalledProgramsService _programsService = new();
    private readonly ProgramUninstallService _uninstallService;
    private readonly ResidualCleanupService _residuals = new();
    private readonly AppDialogHost _dialog;
    private readonly DispatcherTimer _searchDebounceTimer;
    private IReadOnlyList<InstalledProgram> _programs = [];
    private IReadOnlyList<InstalledProgram> _visiblePrograms = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _residualCleanupCancellation;
    private bool _uninstallRunning;
    private bool _hasLoadedOnce;
    private ProgramSortMode _sortMode = ProgramSortMode.LeastRecentlyUsed;
    private SortDirection _sortDirection = SortDirection.Ascending;
    private bool _sortWasSelected;

    public ProgramsView(AppDialogHost dialog)
    {
        InitializeComponent();
        _dialog = dialog;
        _uninstallService = new ProgramUninstallService(_programsService);
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilters();
        };
        Loaded += async (_, _) =>
        {
            if (!_hasLoadedOnce)
            {
                await ReloadAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _loadCancellation?.Cancel();
            _residualCleanupCancellation?.Cancel();
        };
    }

    private async Task ReloadAsync()
    {
        _searchDebounceTimer.Stop();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        UpdateActionButtons();
        SummaryText.Text = "Читаем список программ и журнал запусков...";
        try
        {
            _programs = await _programsService.LoadAsync(_loadCancellation.Token);
            _hasLoadedOnce = true;
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLogService.Write("Ошибка чтения списка программ", exception);
            SummaryText.Text = "Не удалось прочитать список программ. Часть данных недоступна текущему пользователю.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private int GetSelectedUnusedDays()
    {
        if (IdleFilterCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string text &&
            int.TryParse(text, out var days))
        {
            return days;
        }

        return 0;
    }

    private string? GetSelectedArchitecture()
    {
        return ArchFilterCombo?.SelectedItem is ComboBoxItem item && item.Tag is string arch
            ? arch
            : null;
    }

    private void ApplyFilters()
    {
        var now = DateTimeOffset.Now;
        var unusedDays = GetSelectedUnusedDays();
        var arch = GetSelectedArchitecture();
        var filtered = InstalledProgramsService.Filter(_programs, SearchBox.Text, unusedDays, now, arch);
        var sorted = InstalledProgramsService.Sort(filtered, _sortMode, _sortDirection);
        _visiblePrograms = sorted;
        ProgramsList.ItemsSource = sorted.Select(program => new ProgramRow(
            program.Name,
            program.Publisher,
            program.Architecture,
            program.EstimatedBytes > 0 ? ByteSizeFormatter.Format(program.EstimatedBytes) : "—",
            program.InstalledAt is null ? "—" : program.InstalledAt.Value.LocalDateTime.ToString("dd.MM.yyyy"),
            program.LastUsedAt is null ? "нет данных" : program.LastUsedAt.Value.LocalDateTime.ToString("dd.MM.yyyy"),
            FormatIdle(program.LastUsedAt, now),
            program.LastUsedSource,
            program)).ToArray();
        ExportProgramsButton.IsEnabled = sorted.Count > 0;
        UpdateActionButtons();

        var withUsage = _programs.Count(program => program.LastUsedSource == "журнал запусков");
        var totalBytes = filtered.Sum(program => program.EstimatedBytes);
        SummaryText.Text = $"Показано {sorted.Count:N0} из {_programs.Count:N0} программ · примерный объём {ByteSizeFormatter.Format(totalBytes)} · " +
            $"дата запуска известна для {withUsage:N0}. Для остальных показана дата установки или «нет данных».";
    }

    private static string FormatIdle(DateTimeOffset? lastUsed, DateTimeOffset now)
    {
        if (lastUsed is null)
        {
            return "—";
        }

        var days = (int)Math.Max(0, (now - lastUsed.Value).TotalDays);
        return days == 0 ? "сегодня" : $"{days:N0} дн.";
    }

    private void ProgramColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || header.Tag is not string tag ||
            !Enum.TryParse<ProgramSortMode>(tag, out var mode))
        {
            return;
        }

        if (_sortWasSelected && _sortMode == mode)
        {
            _sortDirection = _sortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
        }
        else
        {
            _sortMode = mode;
            _sortDirection = mode is ProgramSortMode.LargestFirst or ProgramSortMode.NewestInstall
                ? SortDirection.Descending
                : SortDirection.Ascending;
            _sortWasSelected = true;
        }

        UpdateSortIndicator(header);
        _searchDebounceTimer.Stop();
        ApplyFilters();
    }

    private void UpdateSortIndicator(GridViewColumnHeader activeHeader)
    {
        foreach (var header in new[] { NameHeader, PublisherHeader, SizeHeader, InstalledHeader, LastUsedHeader, IdleHeader, SourceHeader })
        {
            var text = header.Content?.ToString()?.TrimEnd(' ', '▲', '▼') ?? string.Empty;
            header.Content = text;
        }

        var activeText = activeHeader.Content?.ToString()?.TrimEnd(' ', '▲', '▼') ?? string.Empty;
        activeHeader.Content = $"{activeText} {(_sortDirection == SortDirection.Ascending ? '▲' : '▼')}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    private void IdleFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
            ApplyFilters();
        }
    }

    private void ArchFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
            ApplyFilters();
        }
    }

    private void ProgramsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var selected = SelectedProgram();
        var canAct = selected is not null && !_uninstallRunning;
        UninstallButton.IsEnabled = canAct;
        ScanResidualsButton.IsEnabled = canAct;
        OpenFolderButton.IsEnabled = canAct && !string.IsNullOrWhiteSpace(selected!.InstallLocation) && Directory.Exists(selected.InstallLocation);
        RegistryKeyBox.Text = selected is null || string.IsNullOrWhiteSpace(selected.RegistryKeyPath)
            ? "Выберите программу, чтобы увидеть путь в реестре"
            : selected.RegistryKeyPath;
        UninstallCommandBox.Text = selected is null || string.IsNullOrWhiteSpace(selected.UninstallString)
            ? "—"
            : selected.UninstallString;
    }

    private async void ProgramsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedProgram() is not null && !_uninstallRunning)
        {
            await UninstallSelectedAsync();
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e) => await UninstallSelectedAsync();

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var program = SelectedProgram();
        if (program is null || string.IsNullOrWhiteSpace(program.InstallLocation) || !Directory.Exists(program.InstallLocation))
        {
            await _dialog.ShowMessageAsync("Папка установки", "Папка установки для этой программы не указана в реестре или отсутствует на диске.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{program.InstallLocation}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            AppLogService.Write($"Не удалось открыть папку установки «{program.Name}»", exception);
            await _dialog.ShowMessageAsync("Папка установки", "Не удалось открыть Проводник для выбранной папки.");
        }
    }

    private async void ScanResidualsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_residualCleanupCancellation is not null)
        {
            _residualCleanupCancellation.Cancel();
            return;
        }

        var program = SelectedProgram();
        if (program is null || _uninstallRunning)
        {
            return;
        }

        _uninstallRunning = true;
        UpdateActionButtons();
        RefreshButton.IsEnabled = false;
        SummaryText.Text = $"Ищем файлы и связанные папки «{program.Name}»...";
        try
        {
            var files = await Task.Run(() => _residuals.Find(program));
            if (files.Count == 0)
            {
                await _dialog.ShowMessageAsync("Поиск остатков", $"Для «{program.Name}» не найдено доступных папок установки или связанных папок в профиле.");
                return;
            }

            if (_programsService.IsStillInstalled(program))
            {
                var bytes = files.Sum(file => file.Size);
                var roots = files.Select(file => file.Root).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
                await _dialog.ShowMessageAsync(
                    "Связанные данные программы",
                    $"Программа «{program.Name}» сейчас установлена.\n\nНайдено связанных файлов: {files.Count:N0} ({ByteSizeFormatter.Format(bytes)}).\nПапки: {string.Join("; ", roots)}\n\nЧтобы безопасно удалить программу и её остатки, нажмите «Удалить программу».");
                return;
            }

            var outcome = new UninstallOutcome(false, null, false, string.Empty, files, _residuals.FindRegistryEntries(program));
            await OfferResidualCleanupAsync(program, outcome);
            await ReloadAsync();
        }
        finally
        {
            _uninstallRunning = false;
            RefreshButton.IsEnabled = true;
            UpdateActionButtons();
            ApplyFilters();
        }
    }

    private InstalledProgram? SelectedProgram() => (ProgramsList.SelectedItem as ProgramRow)?.Program;

    private async Task UninstallSelectedAsync()
    {
        var program = SelectedProgram();
        if (program is null || _uninstallRunning)
        {
            return;
        }

        var command = ProgramUninstallService.Parse(program.UninstallString);
        if (command is null)
        {
            await _dialog.ShowMessageAsync(
                "Нет команды удаления",
                $"Для «{program.Name}» в реестре Windows нет команды удаления. Такую программу нужно удалять её собственным деинсталлятором или через окно Windows.");
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            "Удаление программы",
            $"Запустить удаление «{program.Name}»?\n\nCleaner ничего не удаляет сам: он запускает деинсталлятор производителя, который может запросить права администратора и своё подтверждение.\n\nКоманда: {command.Display}",
            "Запустить удаление");
        if (!confirmed)
        {
            return;
        }

        _uninstallRunning = true;
        UpdateActionButtons();
        RefreshButton.IsEnabled = false;
        SummaryText.Text = $"Запущен деинсталлятор «{program.Name}». Завершите удаление в его окне.";
        UninstallOutcome outcome;
        try
        {
            outcome = await _uninstallService.RunAsync(program);
        }
        finally
        {
            _uninstallRunning = false;
            RefreshButton.IsEnabled = true;
            UpdateActionButtons();
        }

        AppLogService.Write($"Удаление «{program.Name}»: запущено={outcome.Started}, код={outcome.ExitCode?.ToString() ?? "нет"}, осталась={outcome.StillInstalled}");
        await OfferResidualCleanupAsync(program, outcome);
        await _dialog.ShowMessageAsync("Удаление программы", outcome.Message);
        await ReloadAsync();
    }

    private async Task OfferResidualCleanupAsync(InstalledProgram program, UninstallOutcome outcome)
    {
        if (outcome.ResidualFiles.Count == 0 && outcome.ResidualRegistryEntries.Count == 0)
        {
            return;
        }

        var bytes = outcome.ResidualFiles.Sum(file => file.Size);
        var paths = outcome.ResidualFiles
            .Select(file => file.Root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        var preview = paths.Length == 0 ? string.Empty : $"\n\nПапки: {string.Join("; ", paths)}";
        var registryWarning = outcome.ResidualRegistryEntries.Count == 0
            ? string.Empty
            : "\n\nТакже будут удалены точные оставшиеся записи этой программы из раздела деинсталляции Windows. Это уберёт её из списка установленных.";
        var confirmed = await _dialog.ConfirmAsync(
            "Найдены остатки программы",
            $"После удаления «{program.Name}» найдены файлы: {outcome.ResidualFiles.Count:N0} ({ByteSizeFormatter.Format(bytes)}) и записи реестра: {outcome.ResidualRegistryEntries.Count:N0}.{preview}{registryWarning}\n\nУдалить найденные остатки? Занятые или защищённые объекты будут пропущены.",
            "Удалить остатки");
        if (!confirmed)
        {
            return;
        }

        var wasRunning = _uninstallRunning;
        _uninstallRunning = true;
        UpdateActionButtons();
        RefreshButton.IsEnabled = false;
        _residualCleanupCancellation = new CancellationTokenSource();
        ScanResidualsButton.Content = "Отменить";
        ScanResidualsButton.IsEnabled = true;

        var progress = new Progress<CleanupProgress>(p =>
            SummaryText.Text = $"Удаление остатков «{program.Name}»: удалено {p.DeletedFiles:N0} из {outcome.ResidualFiles.Count:N0}, пропущено {p.SkippedFiles:N0} ({ByteSizeFormatter.Format(p.ReclaimedBytes)})...");

        ResidualCleanupResult files;
        try
        {
            files = await _residuals.DeleteAsync(outcome.ResidualFiles, _residualCleanupCancellation.Token, progress);
        }
        catch (OperationCanceledException)
        {
            AppLogService.Write($"Удаление остатков «{program.Name}» отменено пользователем");
            await _dialog.ShowMessageAsync("Очистка остатков", "Удаление остатков отменено. Уже удалённые файлы не восстанавливаются.");
            return;
        }
        finally
        {
            _residualCleanupCancellation.Dispose();
            _residualCleanupCancellation = null;
            ScanResidualsButton.Content = "Найти остатки";
            _uninstallRunning = wasRunning;
            if (!wasRunning)
            {
                RefreshButton.IsEnabled = true;
                UpdateActionButtons();
            }
        }

        var registry = _residuals.DeleteRegistryEntries(outcome.ResidualRegistryEntries);
        AppLogService.Write($"Остатки «{program.Name}»: файлов удалено={files.DeletedFiles}, пропущено={files.SkippedFiles}, байт={files.DeletedBytes}, записей реестра удалено={registry.DeletedEntries}, пропущено={registry.SkippedEntries}");
        var reasons = FormatCleanupReasons(files.SkippedByReason, registry.SkippedByReason);
        var advice = FormatCleanupAdvice(files.SkippedByReason, registry.SkippedByReason);
        await _dialog.ShowMessageAsync(
            "Очистка остатков",
            $"Удалено файлов: {files.DeletedFiles:N0} ({ByteSizeFormatter.Format(files.DeletedBytes)}), пропущено: {files.SkippedFiles:N0}. Записей реестра удалено: {registry.DeletedEntries:N0}, пропущено: {registry.SkippedEntries:N0}." + reasons + advice);
    }

    private static string FormatCleanupReasons(IReadOnlyDictionary<CleanupSkipReason, int> fileReasons, IReadOnlyDictionary<CleanupSkipReason, int> registryReasons)
    {
        var all = fileReasons.Concat(registryReasons)
            .GroupBy(item => item.Key)
            .Select(group => (Reason: group.Key, Count: group.Sum(item => item.Value)))
            .Where(item => item.Reason != CleanupSkipReason.None)
            .OrderByDescending(item => item.Count)
            .ToArray();
        return all.Length == 0 ? string.Empty : $"\n\nПричины пропуска:\n{string.Join("\n", all.Select(item => $"• {CleanupReasonText(item.Reason)}: {item.Count:N0}"))}";
    }

    private static string FormatCleanupAdvice(IReadOnlyDictionary<CleanupSkipReason, int> fileReasons, IReadOnlyDictionary<CleanupSkipReason, int> registryReasons)
    {
        var reasons = fileReasons.Keys.Concat(registryReasons.Keys).ToHashSet();
        var advice = new List<string>();
        if (reasons.Contains(CleanupSkipReason.AccessDenied)) advice.Add("запустите Cleaner от имени администратора");
        if (reasons.Contains(CleanupSkipReason.FileInUse)) advice.Add("закройте программу, использующую файлы, и повторите очистку");
        if (reasons.Contains(CleanupSkipReason.NotFound) || reasons.Contains(CleanupSkipReason.Changed)) advice.Add("повторно обновите список остатков");
        if (reasons.Contains(CleanupSkipReason.ReparsePoint)) advice.Add("ссылки и junction пропущены намеренно для защиты данных");
        return advice.Count == 0 ? string.Empty : $"\n\nЧто сделать: {string.Join("; ", advice)}.";
    }

    private static string CleanupReasonText(CleanupSkipReason reason) => reason switch
    {
        CleanupSkipReason.AccessDenied => "нет прав доступа",
        CleanupSkipReason.FileInUse => "файл занят другим процессом",
        CleanupSkipReason.NotFound => "объект уже отсутствует",
        CleanupSkipReason.ReparsePoint => "ссылка или junction",
        CleanupSkipReason.Changed => "запись изменилась после проверки",
        CleanupSkipReason.TooNew => "файл младше заданного возраста",
        _ => "ошибка Windows"
    };

    private async void ExportProgramsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_visiblePrograms.Count == 0)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт списка программ",
            Filter = "CSV (разделители — запятые)|*.csv|JSON (*.json)|*.json",
            FileName = $"cleaner-programs-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ReportExportService.ExportProgramsJson(dialog.FileName, _visiblePrograms);
            }
            else
            {
                ReportExportService.ExportProgramsCsv(dialog.FileName, _visiblePrograms);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogService.Write("Ошибка экспорта списка программ", exception);
            await _dialog.ShowMessageAsync("Cleaner", "Не удалось сохранить файл экспорта. Проверьте права доступа.");
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OpenWindowsAppsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            AppLogService.Write("Не удалось открыть параметры Windows", exception);
            await _dialog.ShowMessageAsync("Cleaner", "Не удалось открыть окно Windows. Откройте «Параметры → Приложения» вручную.");
        }
    }

    private sealed record ProgramRow(string Name, string Publisher, string Arch, string Size, string Installed, string LastUsed, string Idle, string Source, InstalledProgram Program);
}
