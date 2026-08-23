using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace Cleaner;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.Write("Необработанная ошибка интерфейса", e.Exception);
        MessageBox.Show(
            "Произошла ошибка приложения. Подробности сохранены в журнале Cleaner.",
            "Cleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLogService.Write("Критическая ошибка приложения", exception);
        }
        else
        {
            AppLogService.Write("Критическая ошибка приложения", new Exception(e.ExceptionObject?.ToString()));
        }
    }
}
