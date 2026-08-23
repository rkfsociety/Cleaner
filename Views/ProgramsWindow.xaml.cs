using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Cleaner;

public partial class ProgramsWindow : Window
{
    private readonly InstalledProgramsService _programsService = new();
    private IReadOnlyList<InstalledProgram> _programs = [];
    private CancellationTokenSource? _loadCancellation;

    public ProgramsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => _loadCancellation?.Cancel();
    }

    private async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
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
            program.LastUsedSource)).ToArray();

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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OpenWindowsAppsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageBox.Show("Не удалось открыть окно Windows. Откройте «Параметры → Приложения» вручную.", "Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (FileNotFoundException)
        {
            MessageBox.Show("Не удалось открыть окно Windows. Откройте «Параметры → Приложения» вручную.", "Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ProgramRow(string Name, string Publisher, string Size, string Installed, string LastUsed, string Idle, string Source);
}
