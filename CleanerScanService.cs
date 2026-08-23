using System.IO;

namespace Cleaner;

public sealed record ScanFile(string Path, long Bytes);

public sealed record ScanResult(IReadOnlyList<ScanFile> UserTempFiles, IReadOnlyList<ScanFile> WindowsTempFiles, RecycleBinInfo RecycleBin)
{
    public long UserTempBytes => UserTempFiles.Sum(file => file.Bytes);
    public long WindowsTempBytes => WindowsTempFiles.Sum(file => file.Bytes);
    public long TotalBytes => UserTempBytes + WindowsTempBytes + RecycleBin.Bytes;
    public int TotalFiles => UserTempFiles.Count + WindowsTempFiles.Count + RecycleBin.Items;
}

public sealed class CleanerScanService
{
    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => new ScanResult(
            ScanDirectory(Path.GetTempPath(), cancellationToken),
            ScanDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), cancellationToken),
            new RecycleBinService().GetInfo()), cancellationToken);
    }

    public Task<int> DeleteAsync(ScanResult result, bool deleteUserTemp, bool deleteWindowsTemp, bool deleteRecycleBin, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var deleted = 0;
            if (deleteUserTemp)
            {
                deleted += DeleteFiles(result.UserTempFiles, Path.GetTempPath(), cancellationToken);
            }

            if (deleteWindowsTemp)
            {
                deleted += DeleteFiles(result.WindowsTempFiles, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), cancellationToken);
            }

            if (deleteRecycleBin && result.RecycleBin.Items > 0 && new RecycleBinService().Empty())
            {
                deleted += result.RecycleBin.Items;
            }

            return deleted;
        }, cancellationToken);
    }

    private static List<ScanFile> ScanDirectory(string path, CancellationToken cancellationToken)
    {
        var files = new List<ScanFile>();
        if (!Directory.Exists(path))
        {
            return files;
        }

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
                        files.Add(new ScanFile(file, new FileInfo(file).Length));
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
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
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return files;
    }

    private static int DeleteFiles(IEnumerable<ScanFile> files, string root, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file.Path);
                if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                {
                    continue;
                }

                File.Delete(fullPath);
                deleted++;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return deleted;
    }
}
