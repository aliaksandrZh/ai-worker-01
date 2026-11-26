using System.Net;
using PiAgent.Events;
using PiAgent.Publisher;
using PiAgent.Publisher.Github;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// The production GitHub write adapter: 422 "already exists" on PR create resolves the open PR
/// by head (create-or-get); a plain invalid-head 422 (branch truly absent) stays permanent.
/// </summary>
public sealed class RestGithubWriteApiTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.Add("X-RateLimit-Remaining", "4999");
        return response;
    }

    private static readonly PublishPr Pr = new(
        PublishPrKind.Create, "o", "r", WorkflowId: 1, IdempotencyKey: "pr:1:coder:1",
        HeadBranch: "agent/1-fix", BaseBranch: "main", Title: "t", Body: "b", PrNumber: null);

    [Fact]
    public async Task Already_exists_422_resolves_the_open_pr_by_head()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/repos/o/r/pulls?"))
                return Json(HttpStatusCode.OK,
                    """[{"number":7,"head":{"ref":"agent/1-fix"}}]""");
            return Json(HttpStatusCode.UnprocessableEntity,
                """{"message":"Validation Failed","errors":[{"resource":"PullRequest","field":"head","code":"invalid","message":"A pull request already exists for o:agent/1-fix."}],"documentation_url":"https://docs.github.com/rest/pulls/pulls","status":"422"}""");
        });
        var api = new RestGithubWriteApi(new HttpClient(handler), "pat");

        var number = await api.CreateOrUpdatePrAsync(Pr, CancellationToken.None);

        Assert.Equal(7, number);
        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.PathAndQuery == "/repos/o/r/pulls?head=o:agent/1-fix&state=open");
    }

    [Fact]
    public async Task Plain_invalid_head_422_stays_permanent()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"PullRequest","field":"head","code":"invalid"}],"documentation_url":"https://docs.github.com/rest/pulls/pulls","status":"422"}"""));
        var api = new RestGithubWriteApi(new HttpClient(handler), "pat");

        await Assert.ThrowsAsync<GithubPermanentException>(
            () => api.CreateOrUpdatePrAsync(Pr, CancellationToken.None));
    }

    [Fact]
    public async Task Already_exists_422_without_a_matching_open_pr_fails_permanently()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/repos/o/r/pulls?"))
                return Json(HttpStatusCode.OK, "[]");
            return Json(HttpStatusCode.UnprocessableEntity,
                """{"message":"Validation Failed","errors":[{"resource":"PullRequest","field":"head","code":"invalid","message":"A pull request already exists for o:agent/1-fix."}],"documentation_url":"https://docs.github.com/rest/pulls/pulls","status":"422"}""");
        });
        var api = new RestGithubWriteApi(new HttpClient(handler), "pat");

        await Assert.ThrowsAsync<GithubPermanentException>(
            () => api.CreateOrUpdatePrAsync(Pr, CancellationToken.None));
    }
}
