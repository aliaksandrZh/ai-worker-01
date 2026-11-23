using PiAgent.OutboxWorker.GitHub;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Net;
using System.Text.Json;
using PiAgent.OutboxWorker;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// GitHubWriteClient wire behavior (task 03 plan): POST/GET comment endpoints, bearer PAT header
/// from ticket-02 discipline, rate-limit header capture for decision-67 classification, and the
/// never-log-the-token rule — even if a (stubbed, hostile) response echoes it back.
/// </summary>
public class GitHubWriteClientTests
{
    private const string Pat = "s3cret-write-pat";

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is { } content)
                Bodies.Add(await content.ReadAsStringAsync(cancellationToken));
            return await Task.FromResult(responder(request, cancellationToken));
        }
    }

    private static GitHubWriteClient NewClient(StubHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubWriteClient(httpClient, Pat);
    }

    [Fact]
    public async Task CreateComment_ParsesId_AndSendsMarkerBody()
    {
        const string body = "<!-- pi-agent:outbox:key-1 -->\n\nTask failed: /code";
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id": 987654, "body": "x"}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var client = NewClient(handler);

        var commentId = await client.CreateCommentAsync("aliaksandrzh", "04", 42, body, CancellationToken.None);

        Assert.Equal(987654, commentId);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/aliaksandrzh/04/issues/42/comments", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(Pat, request.Headers.Authorization?.Parameter);
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.First().ToString());
        Assert.Equal("pi-agent-outbox-worker", request.Headers.UserAgent.Single().ToString());
        var sent = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(body, sent.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task CreateComment_Status4xx_CarriesStatusCode()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"message": "Validation Failed"}""", System.Text.Encoding.UTF8, "application/json"),
        });

        var exception = await Assert.ThrowsAsync<GitHubDeliveryException>(
            () => NewClient(handler).CreateCommentAsync("o", "r", 1, "body", CancellationToken.None));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.Status);
        Assert.Null(exception.RateLimitRemaining);
        Assert.Null(exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task CreateComment_RateLimitHeaders_Captured()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message": "rate limited"}"""),
        };
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("Retry-After", "60");
        var handler = new StubHandler((_, _) => response);

        var exception = await Assert.ThrowsAsync<GitHubDeliveryException>(
            () => NewClient(handler).CreateCommentAsync("o", "r", 1, "body", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.Status);
        Assert.Equal(0, exception.RateLimitRemaining);
        Assert.Equal(60, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task CreateComment_Regular403_HasNoRateLimitFlags()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message": "write access to repository denied"}""", System.Text.Encoding.UTF8, "application/json"),
        });

        var exception = await Assert.ThrowsAsync<GitHubDeliveryException>(
            () => NewClient(handler).CreateCommentAsync("o", "r", 1, "body", CancellationToken.None));

        Assert.Null(exception.RateLimitRemaining);
        Assert.Null(exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task CreateComment_NetworkFailure_IsAmbiguous()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection reset mid-body"));

        var exception = await Assert.ThrowsAsync<GitHubDeliveryException>(
            () => NewClient(handler).CreateCommentAsync("o", "r", 1, "body", CancellationToken.None));

        Assert.Null(exception.Status);
        Assert.Contains("connection reset", exception.Message);
    }

    [Fact]
    public async Task Exceptions_NeverContainThePat()
    {
        // hostile stub: the response body echoes the token back — the exception must redact it.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($$"""{"message": "Bad credentials ({{"s3cret-write-pat"}})" }""", System.Text.Encoding.UTF8, "application/json"),
        });

        var exception = await Assert.ThrowsAsync<GitHubDeliveryException>(
            () => NewClient(handler).CreateCommentAsync("o", "r", 1, "body", CancellationToken.None));

        Assert.DoesNotContain(Pat, exception.Message);
    }

    [Fact]
    public async Task ListBodies_ReturnsBodies()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/repos/o/r/issues/7/comments?per_page=100&sort=created&direction=desc", request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id": 2, "body": "newest"}, {"id": 1, "body": "older"}]""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var bodies = await NewClient(handler).ListLatestCommentBodiesAsync("o", "r", 7, CancellationToken.None);

        Assert.Equal(["newest", "older"], bodies);
    }
}
