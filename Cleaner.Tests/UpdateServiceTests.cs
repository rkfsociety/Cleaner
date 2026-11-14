using System.Net;
using System.Net.Http;
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

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
