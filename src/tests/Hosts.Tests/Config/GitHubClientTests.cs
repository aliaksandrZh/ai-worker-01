using System.Net;
using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// GitHub REST calls for the Config Loader: GET /user (bot identity) and GET /repos/{owner}/{repo}
/// (repository onboarding metadata). Errors are ConfigException naming the call — never the PAT.
/// </summary>
public class GitHubClientTests
{
    // Review-focus input: the PAT itself must never leak into exception messages.
    private const string Pat = "s3cret-pat-value";

    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public readonly List<string?> AuthorizationHeaders = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(path));
        }
    }

    private static GitHubClient FromStub(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") }, Pat);

    [Fact]
    public async Task GetUser_ReturnsLogin()
    {
        var handler = new StubHandler(path => path == "/user"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"login": "pi-bot"}""") }
            : throw new InvalidOperationException($"unexpected path {path}"));
        var login = await FromStub(handler).GetAuthenticatedLoginAsync(CancellationToken.None);
        Assert.Equal("pi-bot", login);
    }

    [Fact]
    public async Task GetUser_AuthHeaderIsBearerPat()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"login": "pi-bot"}"""),
        });
        await FromStub(handler).GetAuthenticatedLoginAsync(CancellationToken.None);
        Assert.Equal($"Bearer {Pat}", handler.AuthorizationHeaders.Single());
    }

    [Fact]
    public async Task GetUser_NonSuccess_ThrowsConfigExceptionWithStatus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var exception = await Assert.ThrowsAsync<ConfigException>(
            () => FromStub(handler).GetAuthenticatedLoginAsync(CancellationToken.None));
        Assert.Contains("GET /user", exception.Message);
        Assert.Contains("401", exception.Message);
        // the proper error names the PAT problem — no fallback exists
        Assert.Contains("revoked, expired, or invalid", exception.Message);
        Assert.Contains("no automatic fallback", exception.Message);
    }

    [Fact]
    public async Task GetRepository_MapsIdFullNameDefaultBranch()
    {
        var handler = new StubHandler(path => path.StartsWith("/repos/example-org/example-repo")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id": 123456, "full_name": "example-org/example-repo", "default_branch": "main", "private": true}"""),
            }
            : throw new InvalidOperationException($"unexpected path {path}"));
        var repository = await FromStub(handler).GetRepositoryAsync("example-org", "example-repo", CancellationToken.None);
        Assert.Equal(123456, repository.Id);
        Assert.Equal("example-org/example-repo", repository.FullName);
        Assert.Equal("main", repository.DefaultBranch);
    }

    [Fact]
    public async Task GetRepository_404_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var exception = await Assert.ThrowsAsync<ConfigException>(
            () => FromStub(handler).GetRepositoryAsync("example-org", "missing", CancellationToken.None));
        Assert.Contains("example-org/missing", exception.Message);
        Assert.Contains("404", exception.Message);
    }

    [Fact]
    public async Task GetRepository_ServerError_ThrowsWithStatus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var exception = await Assert.ThrowsAsync<ConfigException>(
            () => FromStub(handler).GetRepositoryAsync("example-org", "missing", CancellationToken.None));
        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task Exceptions_NeverContainPat()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var exception = await Assert.ThrowsAsync<ConfigException>(
            () => FromStub(handler).GetAuthenticatedLoginAsync(CancellationToken.None));
        Assert.DoesNotContain(Pat, exception.Message);
        var repositoryException = await Assert.ThrowsAsync<ConfigException>(
            () => FromStub(handler).GetRepositoryAsync("example-org", "missing", CancellationToken.None));
        Assert.DoesNotContain(Pat, repositoryException.Message);
    }
}
