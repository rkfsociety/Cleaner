using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cleaner;

/// <summary>
/// Экспортирует списки программ, найденных файлов и истории очисток в CSV и JSON.
/// </summary>
public static class ReportExportService
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void ExportProgramsCsv(string filePath, IEnumerable<InstalledProgram> programs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Publisher,Version,Architecture,EstimatedBytes,InstalledAt,LastUsedAt,LastUsedSource,InstallLocation,RegistryScope,RegistryKeyPath,UninstallString");
        foreach (var program in programs)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(program.Name),
                EscapeCsv(program.Publisher),
                EscapeCsv(program.Version),
                EscapeCsv(program.Architecture),
                program.EstimatedBytes.ToString(),
                EscapeCsv(program.InstalledAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                EscapeCsv(program.LastUsedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                EscapeCsv(program.LastUsedSource),
                EscapeCsv(program.InstallLocation),
                EscapeCsv(program.RegistryScope),
                EscapeCsv(program.RegistryKeyPath),
                EscapeCsv(program.UninstallString)));
        }

        WriteTextFile(filePath, sb.ToString());
    }

    public static void ExportProgramsJson(string filePath, IEnumerable<InstalledProgram> programs)
    {
        var payload = programs.Select(p => new
        {
            p.Name,
            p.Publisher,
            p.Version,
            p.Architecture,
            p.EstimatedBytes,
            InstalledAt = p.InstalledAt?.LocalDateTime.ToString("o"),
            LastUsedAt = p.LastUsedAt?.LocalDateTime.ToString("o"),
            p.LastUsedSource,
            p.InstallLocation,
            p.RegistryScope,
            p.RegistryKeyPath,
            p.UninstallString
        }).ToArray();

        WriteTextFile(filePath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void ExportScanFilesCsv(string filePath, IEnumerable<ScanFile> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Category,Bytes,LastWriteTimeUtc,Path");
        foreach (var file in files)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(file.Category),
                file.Bytes.ToString(),
                EscapeCsv(file.LastWriteTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                EscapeCsv(file.Path)));
        }

        WriteTextFile(filePath, sb.ToString());
    }

    public static void ExportScanFilesJson(string filePath, IEnumerable<ScanFile> files)
    {
        var payload = files.Select(f => new
        {
            f.Category,
            f.Bytes,
            LastWriteTimeUtc = f.LastWriteTimeUtc?.ToString("o"),
            f.Path
        }).ToArray();

        WriteTextFile(filePath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void ExportHistoryCsv(string filePath, IEnumerable<CleanupHistoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Scope,DeletedFiles,ReclaimedBytes");
        foreach (var entry in entries)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(entry.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")),
                EscapeCsv(entry.Scope),
                entry.DeletedFiles.ToString(),
                entry.ReclaimedBytes.ToString()));
        }

        WriteTextFile(filePath, sb.ToString());
    }

    public static void ExportHistoryJson(string filePath, IEnumerable<CleanupHistoryEntry> entries)
    {
        WriteTextFile(filePath, JsonSerializer.Serialize(entries.ToArray(), JsonOptions));
    }

    private static void WriteTextFile(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content, Utf8WithBom);
    }

    internal static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
