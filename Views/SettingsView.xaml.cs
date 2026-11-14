using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Cleaner;

public partial class SettingsView : UserControl
{
    private readonly Action<int> _apply;
    private readonly Action<IReadOnlyList<string>> _applyExclusions;
    private readonly Action _openDrives;
    private readonly AppDialogHost _dialog;
    private readonly ObservableCollection<string> _exclusions;
    private readonly UpdateService _updateService = new();
    private readonly UpdateInstallerService _installerService = new();
    private bool _updateOperationInProgress;

    public SettingsView(
        int minimumFileAgeHours,
        int selectedDriveCount,
        IReadOnlyList<string> permanentExclusions,
        Action<int> apply,
        Action<IReadOnlyList<string>> applyExclusions,
        Action openDrives,
        AppDialogHost dialog)
    {
        InitializeComponent();
        _apply = apply;
        _applyExclusions = applyExclusions;
        _openDrives = openDrives;
        _dialog = dialog;
        _exclusions = new ObservableCollection<string>(permanentExclusions);
        ExclusionsList.ItemsSource = _exclusions;
        AgeFilterCheckBox.IsChecked = minimumFileAgeHours > 0;
        AdminStatusText.Text = $"Права администратора: {(App.IsAdministrator ? "включены" : "не включены")}";
        DriveStatusText.Text = $"Выбрано дисков: {selectedDriveCount}; системный диск: {WindowsDriveService.GetSystemDriveRoot().TrimEnd('\\')}";
        UpdateVersionText.Text = $"Текущая версия: {CleanerVersion.Current}";
        InstallCurrentButton.Visibility = _installerService.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        InstallCurrentButton.IsEnabled = _installerService.CanInstallCurrentExecutable;
        if (_installerService.IsInnoInstalled)
        {
            UpdateStatusText.Text = "Приложение установлено в системе.";
        }
        else if (_installerService.IsInstalled)
        {
            UpdateStatusText.Text = "Приложение запускается из папки профиля пользователя.";
        }
        else if (!_installerService.CanInstallCurrentExecutable)
        {
            UpdateStatusText.Text = "Установка доступна из автономного Cleaner.exe.";
        }
    }

    private void AgeFilterCheckBox_Click(object sender, RoutedEventArgs e) => SaveCurrentAgeSetting();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveCurrentAgeSetting();

    private void SaveCurrentAgeSetting()
    {
        _apply(AgeFilterCheckBox.IsChecked == true ? 24 : 0);
        SaveStatusText.Text = $"Сохранено в {DateTime.Now:HH:mm}";
    }

    private void DrivesButton_Click(object sender, RoutedEventArgs e) => _openDrives();

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateOperationInProgress) return;
        SetUpdateOperationInProgress(true);
        UpdateStatusText.Text = "Проверяем последний релиз на GitHub...";

        try
        {
            var release = await _updateService.GetLatestReleaseAsync();
            if (release is null)
            {
                UpdateStatusText.Text = "Опубликованных релизов пока нет.";
                await _dialog.ShowMessageAsync("Обновления Cleaner", "Пока нет опубликованного релиза Cleaner.");
                return;
            }

            if (release.Version.CompareTo(CleanerVersion.Current) <= 0)
            {
                UpdateStatusText.Text = $"Установлена последняя версия {CleanerVersion.Current}.";
                await _dialog.ShowMessageAsync("Обновления Cleaner", $"Установлена последняя версия {CleanerVersion.Current}.");
                return;
            }

            var useInstallerUpdate = _installerService.IsInnoInstalled &&
                release.Installer is not null &&
                release.InstallerChecksum is not null;
            UpdateStatusText.Text = $"Доступна версия {release.Version}.";
            var confirmPrompt = useInstallerUpdate
                ? $"Доступна версия {release.Version}. Скачать установщик, проверить SHA-256 и обновить Cleaner?"
                : $"Доступна версия {release.Version}. Скачать, проверить SHA-256 и установить её в профиль пользователя?";
            var install = await _dialog.ConfirmAsync(
                "Обновление Cleaner",
                confirmPrompt,
                "Установить",
                "Позже");
            if (!install) return;

            UpdateStatusText.Text = "Скачиваем обновление...";
            var progress = new Progress<int>(percent => UpdateStatusText.Text = $"Скачиваем обновление: {percent}%");
            if (useInstallerUpdate)
            {
                var stagedInstaller = await _updateService.DownloadAndVerifyInstallerAsync(release, progress);
                UpdateStatusText.Text = "Запускаем установщик обновления... Cleaner перезапустится автоматически.";
                try
                {
                    _installerService.StartInstallerUpdate(stagedInstaller);
                }
                catch
                {
                    _installerService.DeleteStagedExecutable(stagedInstaller);
                    throw;
                }
            }
            else
            {
                var stagedExecutable = await _updateService.DownloadAndVerifyAsync(release, progress);
                UpdateStatusText.Text = "Запускаем установку... Cleaner перезапустится из папки профиля.";
                try
                {
                    _installerService.StartInstall(stagedExecutable);
                }
                catch
                {
                    _installerService.DeleteStagedExecutable(stagedExecutable);
                    throw;
                }
            }

            Application.Current.Shutdown();
        }
        catch (UpdateException exception)
        {
            AppLogService.Write("Не удалось проверить или установить обновление Cleaner", exception);
            UpdateStatusText.Text = exception.Message;
            await _dialog.ShowMessageAsync("Обновление Cleaner", exception.Message);
        }
        catch (Exception exception)
        {
            AppLogService.Write("Не удалось проверить или установить обновление Cleaner", exception);
            UpdateStatusText.Text = "Не удалось выполнить обновление.";
            await _dialog.ShowMessageAsync("Обновление Cleaner", "Не удалось проверить или установить обновление. Подробности сохранены в журнале Cleaner.");
        }
        finally
        {
            SetUpdateOperationInProgress(false);
        }
    }

    private async void InstallCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateOperationInProgress) return;
        var install = await _dialog.ConfirmAsync(
            "Установка Cleaner",
            $"Установить версию {CleanerVersion.Current} в папку профиля пользователя? Cleaner создаст ярлык в меню «Пуск» и перезапустится из новой папки. Исходный portable-файл останется на месте.",
            "Установить",
            "Отмена");
        if (!install) return;

        SetUpdateOperationInProgress(true);
        UpdateStatusText.Text = "Подготавливаем установку...";
        string? stagedExecutable = null;
        try
        {
            stagedExecutable = _installerService.StageCurrentExecutable();
            _installerService.StartInstall(stagedExecutable);
            UpdateStatusText.Text = "Cleaner перезапустится из папки профиля.";
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            if (stagedExecutable is not null)
            {
                _installerService.DeleteStagedExecutable(stagedExecutable);
            }
            AppLogService.Write("Не удалось установить Cleaner в профиль пользователя", exception);
            UpdateStatusText.Text = "Не удалось установить Cleaner.";
            await _dialog.ShowMessageAsync("Установка Cleaner", "Не удалось установить Cleaner. Подробности сохранены в журнале Cleaner.");
        }
        finally
        {
            SetUpdateOperationInProgress(false);
        }
    }

    private void SetUpdateOperationInProgress(bool inProgress)
    {
        _updateOperationInProgress = inProgress;
        CheckUpdatesButton.IsEnabled = !inProgress;
        InstallCurrentButton.IsEnabled = !inProgress && _installerService.CanInstallCurrentExecutable;
    }

    private void AddExclusionFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для белого списка исключений"
        };
        if (dialog.ShowDialog() == true)
        {
            AddExclusionPath(dialog.FolderName);
        }
    }

    private void AddExclusionFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл для белого списка исключений",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                AddExclusionPath(file);
            }
        }
    }

    private void AddExclusionPath(string path)
    {
        var normalized = ExclusionListService.NormalizePath(path);
        if (normalized is null)
        {
            return;
        }

        if (!_exclusions.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _exclusions.Add(normalized);
            SaveExclusions();
        }
    }

    private void RemoveExclusionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExclusionsList.SelectedItem is string selected)
        {
            _exclusions.Remove(selected);
            SaveExclusions();
        }
    }

    private void ExclusionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveExclusionButton.IsEnabled = ExclusionsList.SelectedItem is string;
    }

    private void SaveExclusions()
    {
        _applyExclusions(_exclusions.ToArray());
        SaveStatusText.Text = $"Сохранено в {DateTime.Now:HH:mm}";
    }
}
