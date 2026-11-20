using System.Net;
using PiAgent.Poller;
using Xunit;
using PiAgent.Platform.Http;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Poller GitHub REST client (task 04): Link-header pagination, conditional If-None-Match/304,
/// rate-limit classification (403 remaining=0 / 429 → GitHubRateLimitedException carrying reset and
/// Retry-After), plain errors as GitHubReadException with status, and the PAT that must never leak.
/// </summary>
public class GitHubReadClientTests
{
    private const string Pat = "s3cret-read-pat";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public readonly List<string?> AuthorizationHeaders = [];
        public readonly List<string?> EtagHeaders = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            EtagHeaders.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            return Task.FromResult(respond(request));
        }
    }

    private static GitHubReadClient FromStub(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, Pat);

    private static HttpResponseMessage ArrayPage(string[] items, string? etag = null, Uri? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{string.Join(",", items)}]"),
        };
        if (etag is { }) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
        if (next is { }) response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
        return response;
    }

    [Fact]
    public async Task ArrayResponse_ParsedIntoItems()
    {
        var handler = new StubHandler(_ => ArrayPage(["""{"id": 1}""", """{"id": 2}"""]));
        var page = await FromStub(handler).GetPageAsync("repos/o/r/issues", null, CancellationToken.None);
        Assert.False(page.NotModified);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.Items[0].GetProperty("id").GetInt32());
        Assert.Equal(2, page.Items[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task LinkHeader_RelNext_Parsed()
    {
        var handler = new StubHandler(_ => ArrayPage(
            ["""{"id": 1}"""], next: new Uri("https://api.github.com/repos/o/r/issues?per_page=100&page=2")));
        var page = await FromStub(handler).GetPageAsync("repos/o/r/issues", null, CancellationToken.None);
        Assert.NotNull(page.NextPageUri);
        Assert.EndsWith("page=2", page.NextPageUri!.Query);
    }

    [Fact]
    public async Task Etag_SentAsIfNoneMatch()
    {
        var handler = new StubHandler(_ => ArrayPage([], etag: "abc123"));
        var client = FromStub(handler);
        await client.GetPageAsync("repos/o/r/issues", "abc123", CancellationToken.None);
        Assert.Equal("\"abc123\"", handler.EtagHeaders.Single());
    }

    [Fact]
    public async Task NotModified_Returns304Page()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var page = await FromStub(handler).GetPageAsync("repos/o/r/issues", "abc123", CancellationToken.None);
        Assert.True(page.NotModified);
        Assert.Empty(page.Items);
        Assert.Null(page.Etag);
    }

    [Fact]
    public async Task EtagCaptured_Upon200()
    {
        var handler = new StubHandler(_ => ArrayPage([], etag: "abc123"));
        var page = await FromStub(handler).GetPageAsync("repos/o/r/issues", null, CancellationToken.None);
        Assert.Equal("abc123", page.Etag);
    }

    [Fact]
    public async Task Forbidden_WithRemainingZero_IsRateLimited()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message": "rate limit"}"""),
        };
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "1732100000");
        var handler = new StubHandler(_ => response);

        var exception = await Assert.ThrowsAsync<GitHubRateLimitedException>(
            () => FromStub(handler).GetPageAsync("repos/o/r/issues", null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.Status);
        Assert.Equal(0, exception.Remaining);
        Assert.Equal(1732100000, exception.ResetAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task TooManyRequests_IsRateLimited_WithRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{}"""),
        };
        response.Headers.Add("Retry-After", "42");
        var handler = new StubHandler(_ => response);

        var exception = await Assert.ThrowsAsync<GitHubRateLimitedException>(
            () => FromStub(handler).GetPageAsync("repos/o/r/pulls?state=all", null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.Status);
        Assert.Equal(42, exception.RetryAfterSeconds!.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherErrors_AreReadException_WithStatus(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"message": "nope"}"""),
        });
        var exception = await Assert.ThrowsAsync<GitHubReadException>(
            () => FromStub(handler).GetPageAsync("repos/o/r/issues", null, CancellationToken.None));
        Assert.Equal(status, exception.Status);
    }

    [Fact]
    public async Task Exceptions_NeverContainPat()
    {
        // Review-focus input: a (hostile) response echo carrying the PAT never lands in a message.
        var echoing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($"{{\"message\": \"bad credentials with echo {Pat}\"}}"),
        });
        var readException = await Assert.ThrowsAsync<GitHubReadException>(
            () => FromStub(echoing).GetPageAsync("repos/o/r/issues", null, CancellationToken.None));
        Assert.DoesNotContain(Pat, readException.Message);

        var rateLimitEcho = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent($"Bad PAT {Pat} echoed"),
            };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        });
        var rateLimited = await Assert.ThrowsAsync<GitHubRateLimitedException>(
            () => FromStub(rateLimitEcho).GetPageAsync("repos/o/r/issues", null, CancellationToken.None));
        Assert.DoesNotContain(Pat, rateLimited.Message);

        var networkError = await Assert.ThrowsAsync<GitHubReadException>(
            () => FromStub(new StubHandler(_ => throw new HttpRequestException($"socket exploded {Pat}")))
                .GetPageAsync("repos/o/r/issues", null, CancellationToken.None));
        Assert.DoesNotContain(Pat, networkError.Message);
    }
}


