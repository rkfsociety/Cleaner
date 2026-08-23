using System;
using System.IO;

namespace Cleaner;

internal static class AppLogService
{
    private static readonly object SyncRoot = new();

    public static void Write(string message, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cleaner");
            Directory.CreateDirectory(directory);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}: {exception}\r\n";
            lock (SyncRoot)
            {
                File.AppendAllText(Path.Combine(directory, "app.log"), line);
            }
        }
        catch
        {
            // Ошибка записи журнала не должна завершать приложение.
        }
    }
}
