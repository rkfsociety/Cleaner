using System;
using System.IO;

namespace Cleaner;

internal static class AppLogService
{
    private static readonly object SyncRoot = new();
    private const long MaxLogBytes = 1_048_576;

    public static void Write(string message, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cleaner");
            Directory.CreateDirectory(directory);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}: {exception}\r\n";
            var path = Path.Combine(directory, "app.log");
            lock (SyncRoot)
            {
                RollIfNeeded(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Ошибка записи журнала не должна завершать приложение.
        }
    }

    private static void RollIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxLogBytes)
        {
            return;
        }

        var previous = path + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(path, previous);
    }
}
