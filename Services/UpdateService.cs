using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cleaner;

public readonly record struct CleanerVersion(int Major, int Minor, int Patch) : IComparable<CleanerVersion>
{
    private static readonly Regex TagPattern = new(@"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);

    public static CleanerVersion Current { get; } = FromAssemblyVersion();

    public int CompareTo(CleanerVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"v{Major}.{Minor}.{Patch}";

    public static bool TryParseTag(string? value, out CleanerVersion version)
    {
        version = default;
        var match = value is null ? Match.Empty : TagPattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
        {
            return false;
        }

        version = new CleanerVersion(major, minor, patch);
        return true;
    }

    private static CleanerVersion FromAssemblyVersion()
    {
        var version = typeof(CleanerVersion).Assembly.GetName().Version
            ?? throw new InvalidOperationException("В сборке Cleaner не указана версия.");
        if (version.Build < 0)
        {
            throw new InvalidOperationException("Версия Cleaner должна содержать три числовых компонента.");
        }

        return new CleanerVersion(version.Major, version.Minor, version.Build);
    }
}

public sealed record UpdateAsset(Uri DownloadUri);

public sealed record CleanerRelease(CleanerVersion Version, string TagName, UpdateAsset Executable, UpdateAsset Checksum);

public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message)
    {
    }

    public UpdateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class UpdateService
{
    private const string Owner = "rkfsociety";
    private const string Repository = "Cleaner";
    private static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Client = new();
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
    private readonly HttpClient _client;
    private readonly TimeSpan _apiTimeout;

    public UpdateService() : this(Client, DefaultApiTimeout)
    {
    }

    internal UpdateService(HttpClient client, TimeSpan? apiTimeout = null)
    {
        _client = client;
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
    }

    public async Task<CleanerRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_apiTimeout);
        var requestToken = timeout.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.UserAgent.ParseAdd("Cleaner-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                if (IsRateLimitResponse(response))
                {
                    throw new UpdateException("Превышен лимит запросов к GitHub API. Повторите проверку обновлений позже.");
                }

                throw new UpdateException($"Доступ к GitHub API временно ограничен (HTTP {(int)response.StatusCode}). Повторите попытку позже.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"Сервер GitHub вернул ошибку {(int)response.StatusCode}. Повторите проверку обновлений позже.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestToken);
            var root = document.RootElement;
            var tagName = root.GetProperty("tag_name").GetString();
            if (!CleanerVersion.TryParseTag(tagName, out var version))
            {
                throw new InvalidDataException("Последний релиз Cleaner содержит некорректный номер версии.");
            }

            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var executable = FindAsset(assets, "Cleaner.exe");
            var checksum = FindAsset(assets, "Cleaner.exe.sha256");
            return new CleanerRelease(version, tagName!, executable, checksum);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("Превышено время ожидания ответа от GitHub. Проверьте подключение к интернету и повторите попытку.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException("Не удалось подключиться к GitHub. Проверьте подключение к интернету.", exception);
        }
    }

    private static bool IsRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.Headers.RetryAfter is not null)
        {
            return true;
        }

        return response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.Any(value => string.Equals(value.Trim(), "0", StringComparison.Ordinal));
    }

    public async Task<string> DownloadAndVerifyAsync(
        CleanerRelease release,
        IProgress<int>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cleaner",
            "updates",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        var executablePath = Path.Combine(updateRoot, "Cleaner.exe");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var transferToken = timeout.Token;

            var checksumText = await DownloadTextAsync(release.Checksum.DownloadUri, transferToken);
            var expectedHash = checksumText.Trim();
            if (!Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("Файл SHA-256 релиза имеет неверный формат.");
            }

            await DownloadFileAsync(release.Executable.DownloadUri, executablePath, downloadProgress, transferToken);
            await using var executable = File.OpenRead(executablePath);
            var actualHash = await SHA256.HashDataAsync(executable, transferToken);
            var expectedBytes = Convert.FromHexString(expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes))
            {
                throw new InvalidDataException("SHA-256 скачанного Cleaner.exe не совпадает с файлом релиза.");
            }

            return executablePath;
        }
        catch
        {
            try { Directory.Delete(updateRoot, recursive: true); }
            catch (IOException exception) { AppLogService.Write("Не удалось удалить временные файлы обновления", exception); }
            catch (UnauthorizedAccessException exception) { AppLogService.Write("Нет доступа к временным файлам обновления для удаления", exception); }
            throw;
        }
    }

    private static UpdateAsset FindAsset(JsonElement[] assets, string name)
    {
        var matches = assets.Where(asset => string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"В последнем релизе Cleaner должен быть один файл {name}.");
        }

        var url = matches[0].GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/releases/download/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Ссылка на файл {name} ведёт не на релиз Cleaner.");
        }

        return new UpdateAsset(uri);
    }

    private static async Task<string> DownloadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[1025];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > 1024)
        {
            throw new InvalidDataException("Файл SHA-256 превышает допустимый размер.");
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static async Task DownloadFileAsync(Uri uri, string destination, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var contentLength = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long total = 0;
        var lastReportedPercent = -1;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            if (contentLength is > 0)
            {
                var percent = (int)(total * 100d / contentLength.Value);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(Math.Min(percent, 100));
                }
            }
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException("GitHub вернул пустой файл Cleaner.exe.");
        }
    }
}
