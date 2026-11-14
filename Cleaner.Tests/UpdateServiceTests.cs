using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Cleaner;
using Xunit;

namespace Cleaner.Tests;

public class UpdateServiceTests
{
    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsRateLimitUpdateException_OnTooManyRequests()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        var service = new UpdateService(client);

        var exception = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Contains("лимит", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsRateLimitUpdateException_OnForbiddenWithZeroRemaining()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }));
        var service = new UpdateService(client);

        var exception = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Contains("лимит", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsNetworkUpdateException_OnHttpRequestException()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new HttpRequestException("Network is unreachable")));
        var service = new UpdateService(client);

        var exception = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Contains("интернет", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ThrowsTimeoutUpdateException_WhenRequestExceedsApiTimeout()
    {
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new UpdateService(client, TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsAsync<UpdateException>(() => service.GetLatestReleaseAsync());

        Assert.Contains("время ожидания", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesOptionalInstallerAssets_WhenPresent()
    {
        const string json = """
            {
              "tag_name": "v0.0.20",
              "assets": [
                { "name": "DiskPulse.exe", "browser_download_url": "https://github.com/rkfsociety/DiskPulse/releases/download/v0.0.20/DiskPulse.exe" },
                { "name": "DiskPulse.exe.sha256", "browser_download_url": "https://github.com/rkfsociety/DiskPulse/releases/download/v0.0.20/DiskPulse.exe.sha256" },
                { "name": "DiskPulse-Setup.exe", "browser_download_url": "https://github.com/rkfsociety/DiskPulse/releases/download/v0.0.20/DiskPulse-Setup.exe" },
                { "name": "DiskPulse-Setup.exe.sha256", "browser_download_url": "https://github.com/rkfsociety/DiskPulse/releases/download/v0.0.20/DiskPulse-Setup.exe.sha256" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })));
        var service = new UpdateService(client);

        var release = await service.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal(new CleanerVersion(0, 0, 20), release!.Version);
        Assert.NotNull(release.Installer);
        Assert.NotNull(release.InstallerChecksum);
        Assert.EndsWith("/DiskPulse-Setup.exe", release.Installer!.DownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_AllowsReleasesWithoutInstallerAssets()
    {
        const string json = """
            {
              "tag_name": "v0.0.20",
              "assets": [
                { "name": "DiskPulse.exe", "browser_download_url": "https://github.com/rkfsociety/DiskPulse/releases/download/v0.0.20/DiskPulse.exe" },
                { "name": "DiskPulse.exe.sha256", "browser_download_url": "https://github.com/rkfsociety/DiskPulse/releases/download/v0.0.20/DiskPulse.exe.sha256" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })));
        var service = new UpdateService(client);

        var release = await service.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Null(release!.Installer);
        Assert.Null(release.InstallerChecksum);
    }

    [Fact]
    public void IsInnoInstalledAt_DetectsUninstallerMarkersNextToExecutable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cleaner-inno-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var exePath = Path.Combine(tempDir, "DiskPulse.exe");
            File.WriteAllBytes(exePath, []);
            Assert.False(UpdateInstallerService.IsInnoInstalledAt(exePath));

            File.WriteAllBytes(Path.Combine(tempDir, "unins000.exe"), []);
            Assert.False(UpdateInstallerService.IsInnoInstalledAt(exePath));

            File.WriteAllBytes(Path.Combine(tempDir, "unins000.dat"), []);
            Assert.True(UpdateInstallerService.IsInnoInstalledAt(exePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
