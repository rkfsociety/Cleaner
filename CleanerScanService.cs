using System.IO;

namespace Cleaner;

public sealed record ScanResult(long UserTempBytes, int UserTempFiles, long WindowsTempBytes, int WindowsTempFiles)
{
    public long TotalBytes => UserTempBytes + WindowsTempBytes;
    public int TotalFiles => UserTempFiles + WindowsTempFiles;
}

public sealed class CleanerScanService
{
    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => new ScanResult(
            ScanDirectory(Path.GetTempPath(), cancellationToken, out var userFiles),
            userFiles,
            ScanDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), cancellationToken, out var windowsFiles),
            windowsFiles), cancellationToken);
    }

    private static long ScanDirectory(string path, CancellationToken cancellationToken, out int fileCount)
    {
        fileCount = 0;
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                        fileCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if (!new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push(directory);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return totalBytes;
    }
}
