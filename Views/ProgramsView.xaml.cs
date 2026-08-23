using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cleaner;

public partial class ProgramsView : UserControl
{
    private readonly InstalledProgramsService _programsService = new();
    private readonly ProgramUninstallService _uninstallService;
    private readonly ResidualCleanupService _residuals = new();
    private readonly AppDialogHost _dialog;
    private IReadOnlyList<InstalledProgram> _programs = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _uninstallRunning;

    public ProgramsView(AppDialogHost dialog)
    {
        InitializeComponent();
        _dialog = dialog;
        _uninstallService = new ProgramUninstallService(_programsService);
        Loaded += async (_, _) => await ReloadAsync();
        Unloaded += (_, _) => _loadCancellation?.Cancel();
    }

    private async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        SummaryText.Text = "Читаем список программ и журнал запусков...";
        try
        {
            _programs = await _programsService.LoadAsync(_loadCancellation.Token);
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

    private void ApplyFilters()
    {
        var now = DateTimeOffset.Now;
        var filtered = InstalledProgramsService.Filter(_programs, SearchBox.Text, SelectedUnusedDays(), now);
        var sorted = InstalledProgramsService.Sort(filtered, SelectedSortMode());
        ProgramsList.ItemsSource = sorted.Select(program => new ProgramRow(
            program.Name,
            program.Publisher,
            program.EstimatedBytes > 0 ? ByteSizeFormatter.Format(program.EstimatedBytes) : "—",
            program.InstalledAt is null ? "—" : program.InstalledAt.Value.LocalDateTime.ToString("dd.MM.yyyy"),
            program.LastUsedAt is null ? "нет данных" : program.LastUsedAt.Value.LocalDateTime.ToString("dd.MM.yyyy"),
            FormatIdle(program.LastUsedAt, now),
            program.LastUsedSource,
            program)).ToArray();
        UninstallButton.IsEnabled = false;

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

    private ProgramSortMode SelectedSortMode() => SortBox.SelectedIndex switch
    {
        1 => ProgramSortMode.MostRecentlyUsed,
        2 => ProgramSortMode.LargestFirst,
        3 => ProgramSortMode.NewestInstall,
        4 => ProgramSortMode.Name,
        _ => ProgramSortMode.LeastRecentlyUsed
    };

    private int SelectedUnusedDays() => UnusedBox.SelectedIndex switch
    {
        1 => 30,
        2 => 90,
        3 => 180,
        4 => 365,
        _ => 0
    };

    private void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilters();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilters();
        }
    }

    private void ProgramsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UninstallButton.IsEnabled = SelectedProgram() is not null && !_uninstallRunning;
    }

    private async void ProgramsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedProgram() is not null && !_uninstallRunning)
        {
            await UninstallSelectedAsync();
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e) => await UninstallSelectedAsync();

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
        UninstallButton.IsEnabled = false;
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

        var files = _residuals.Delete(outcome.ResidualFiles);
        var registry = _residuals.DeleteRegistryEntries(outcome.ResidualRegistryEntries);
        AppLogService.Write($"Остатки «{program.Name}»: файлов удалено={files.DeletedFiles}, пропущено={files.SkippedFiles}, байт={files.DeletedBytes}, записей реестра удалено={registry.DeletedEntries}, пропущено={registry.SkippedEntries}");
        await _dialog.ShowMessageAsync(
            "Очистка остатков",
            $"Удалено файлов: {files.DeletedFiles:N0} ({ByteSizeFormatter.Format(files.DeletedBytes)}), пропущено: {files.SkippedFiles:N0}. Записей реестра удалено: {registry.DeletedEntries:N0}, пропущено: {registry.SkippedEntries:N0}.");
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

    private sealed record ProgramRow(string Name, string Publisher, string Size, string Installed, string LastUsed, string Idle, string Source, InstalledProgram Program);
}
