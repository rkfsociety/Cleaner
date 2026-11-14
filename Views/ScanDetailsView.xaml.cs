using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Cleaner;

public partial class ScanDetailsView : UserControl
{
    private readonly ScanResult _scan;
    private readonly HashSet<string> _excludedPaths;
    private readonly AppDialogHost _dialog;
    private readonly Action _back;
    private readonly List<ScanFileRow> _allRows;
    private readonly DispatcherTimer _searchDebounceTimer;
    private int _includedCount;
    private long _includedBytes;
    private string _sortColumn = "Size";
    private SortDirection _sortDirection = SortDirection.Descending;

    public ScanDetailsView(ScanResult scan, HashSet<string> excludedPaths, AppDialogHost dialog, Action back)
    {
        InitializeComponent();
        _scan = scan;
        _excludedPaths = excludedPaths;
        _dialog = dialog;
        _back = back;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilterAndSort();
        };
        Unloaded += (_, _) => _searchDebounceTimer.Stop();

        _allRows = new List<ScanFileRow>(scan.UserTempFiles.Count + scan.WindowsTempFiles.Count + scan.DevCacheFilesOrEmpty.Count);
        AddRows(scan.UserTempFiles, "Temp и кэш пользователя");
        AddRows(scan.WindowsTempFiles, "Системный Temp");
        AddRows(scan.DevCacheFilesOrEmpty, "Кэш инструментов разработки");

        var categories = _allRows
            .Select(row => row.Category)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        categories.Insert(0, "Все категории");
        CategoryFilterCombo.ItemsSource = categories;
        CategoryFilterCombo.SelectedIndex = 0;

        ApplyFilterAndSort();
    }

    private void AddRows(IReadOnlyList<ScanFile> files, string fallbackCategory)
    {
        foreach (var file in files)
        {
            var isIncluded = !_excludedPaths.Contains(file.Path);
            if (isIncluded)
            {
                _includedCount++;
                _includedBytes += file.Bytes;
            }

            _allRows.Add(new ScanFileRow(
                string.IsNullOrWhiteSpace(file.Category) ? fallbackCategory : file.Category,
                file.Bytes,
                ByteSizeFormatter.Format(file.Bytes),
                file.Path,
                file.LastWriteTimeUtc,
                isIncluded,
                _excludedPaths,
                OnRowInclusionChanged));
        }
    }

    private void OnRowInclusionChanged(long bytes, bool isIncluded)
    {
        if (isIncluded)
        {
            _includedCount++;
            _includedBytes += bytes;
        }
        else
        {
            _includedCount--;
            _includedBytes -= bytes;
        }
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<ScanFileRow> query = _allRows;
        if (CategoryFilterCombo?.SelectedItem is string selectedCategory &&
            !string.Equals(selectedCategory, "Все категории", StringComparison.Ordinal))
        {
            query = query.Where(row => string.Equals(row.Category, selectedCategory, StringComparison.CurrentCultureIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchBox?.Text))
        {
            var term = SearchBox.Text.Trim();
            query = query.Where(row =>
                row.Path.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                row.Category.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        var descending = _sortDirection == SortDirection.Descending;
        query = _sortColumn switch
        {
            "Category" => descending
                ? query.OrderByDescending(row => row.Category, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(row => row.Bytes)
                : query.OrderBy(row => row.Category, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(row => row.Bytes),
            "Path" => descending
                ? query.OrderByDescending(row => row.Path, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(row => row.Path, StringComparer.CurrentCultureIgnoreCase),
            _ => descending
                ? query.OrderByDescending(row => row.Bytes).ThenBy(row => row.Path, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(row => row.Bytes).ThenBy(row => row.Path, StringComparer.CurrentCultureIgnoreCase)
        };

        var items = query.ToArray();
        FilesList.ItemsSource = items;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        SummaryText.Text = $"Проверка: {_scan.ScannedAt.LocalDateTime:g}. Корзина: {_scan.RecycleBin.Items:N0} объектов ({ByteSizeFormatter.Format(_scan.RecycleBin.Bytes)}). " +
            $"Выбрано файлов Temp и кэшей: {_includedCount:N0} из {_allRows.Count:N0} ({ByteSizeFormatter.Format(_includedBytes)}). Снимите галочку, чтобы оставить файл.";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    private void CategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchDebounceTimer.Stop();
            ApplyFilterAndSort();
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var visibleRows = FilesList.ItemsSource as IEnumerable<ScanFileRow> ?? _allRows;
        var filesToExport = visibleRows
            .Select(row => new ScanFile(row.Path, row.Bytes, row.Category, row.LastWriteTimeUtc))
            .ToArray();
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт отчёта о проверке",
            Filter = "CSV (разделители — запятые)|*.csv|JSON (*.json)|*.json",
            FileName = $"diskpulse-scan-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ReportExportService.ExportScanFilesJson(dialog.FileName, filesToExport);
            }
            else
            {
                ReportExportService.ExportScanFilesCsv(dialog.FileName, filesToExport);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogService.Write("Ошибка экспорта отчёта сканирования", exception);
            await _dialog.ShowMessageAsync("DiskPulse", "Не удалось сохранить файл отчёта. Проверьте права доступа.");
        }
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || header.Tag is not string column)
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        }
        else
        {
            _sortColumn = column;
            _sortDirection = column == "Size" ? SortDirection.Descending : SortDirection.Ascending;
        }

        foreach (var item in new[] { CategoryHeader, SizeHeader, PathHeader })
        {
            var text = item.Content?.ToString()?.TrimEnd(' ', '▲', '▼') ?? string.Empty;
            item.Content = text;
        }

        var activeText = header.Content?.ToString()?.TrimEnd(' ', '▲', '▼') ?? string.Empty;
        header.Content = $"{activeText} {(_sortDirection == SortDirection.Ascending ? '▲' : '▼')}";
        _searchDebounceTimer.Stop();
        ApplyFilterAndSort();
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.ItemsSource is IEnumerable<ScanFileRow> visibleRows)
        {
            foreach (var row in visibleRows)
            {
                row.IsIncluded = true;
            }
        }

        UpdateSummary();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.ItemsSource is IEnumerable<ScanFileRow> visibleRows)
        {
            foreach (var row in visibleRows)
            {
                row.IsIncluded = false;
            }
        }

        UpdateSummary();
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenLocationButton.IsEnabled = FilesList.SelectedItem is ScanFileRow;
    }

    private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not ScanFileRow row)
        {
            return;
        }

        try
        {
            if (File.Exists(row.Path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = true });
            }
            else if (Path.GetDirectoryName(row.Path) is string directory && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            AppLogService.Write($"Не удалось открыть расположение файла {row.Path}", exception);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _back();

    private sealed class ScanFileRow(string category, long bytes, string size, string path, DateTime? lastWriteTimeUtc, bool isIncluded, HashSet<string> excludedPaths, Action<long, bool> onInclusionChanged) : INotifyPropertyChanged
    {
        private bool _isIncluded = isIncluded;

        public string Category { get; } = category;
        public long Bytes { get; } = bytes;
        public string Size { get; } = size;
        public string Path { get; } = path;
        public DateTime? LastWriteTimeUtc { get; } = lastWriteTimeUtc;

        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (_isIncluded == value)
                {
                    return;
                }

                _isIncluded = value;
                if (value)
                {
                    excludedPaths.Remove(Path);
                }
                else
                {
                    excludedPaths.Add(Path);
                }

                onInclusionChanged(Bytes, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
