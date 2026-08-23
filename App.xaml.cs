using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace Cleaner;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static bool IsSystemCleanupSession => Environment.GetCommandLineArgs().Contains("--system-cleanup", StringComparer.OrdinalIgnoreCase);
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
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

    public static bool RestartForSystemCleanup()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--system-cleanup",
                UseShellExecute = true,
                Verb = "runas"
            });
            Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
