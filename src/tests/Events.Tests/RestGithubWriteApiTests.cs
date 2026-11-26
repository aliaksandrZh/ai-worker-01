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
        PublishPrKind.Create, "o", "r", WorkflowId: 1, ItemNumber: 1, IdempotencyKey: "pr:1:coder:1",
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

    [Fact]
    public async Task Submit_review_404_resolves_the_pr_by_head_and_retries_against_it()
    {
        // decision 1.2.2: the first review run can be scheduled before PrPublished lands,
        // so its PrNumber may be the item number — 404 → resolve by head → retry once
        var posts = 0;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/repos/o/r/pulls?"))
                return Json(HttpStatusCode.OK,
                    """[{"number":28,"head":{"ref":"agent/1-fix"}}]""");
            posts++;
            return posts == 1
                ? Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""")
                : Json(HttpStatusCode.OK, """{"id":9001,"number":28}""");
        });
        var api = new RestGithubWriteApi(new HttpClient(handler), "pat");

        var review = new PublishReview("o", "r", PrNumber: 27, "APPROVE", "ok", RunId: 1,
            IdempotencyKey: "review:1", HeadBranch: "agent/1-fix");
        Assert.Equal(9001, await api.SubmitReviewAsync(review, CancellationToken.None));

        Assert.Equal(2, posts);
        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.PathAndQuery == "/repos/o/r/pulls/27/reviews");
        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.PathAndQuery == "/repos/o/r/pulls/28/reviews");
        Assert.Contains(handler.Requests, r =>
            r.RequestUri!.PathAndQuery == "/repos/o/r/pulls?head=o:agent/1-fix&state=open");
    }

    [Fact]
    public async Task Submit_review_404_with_no_open_pr_for_the_head_fails_permanently()
    {
        var posts = 0;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/repos/o/r/pulls?"))
                return Json(HttpStatusCode.OK, "[]");
            posts++;
            return Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        });
        var api = new RestGithubWriteApi(new HttpClient(handler), "pat");

        var review = new PublishReview("o", "r", PrNumber: 27, "APPROVE", "ok", RunId: 1,
            IdempotencyKey: "review:1", HeadBranch: "agent/1-fix");
        await Assert.ThrowsAsync<GithubPermanentException>(
            () => api.SubmitReviewAsync(review, CancellationToken.None));

        Assert.Equal(1, posts); // no blind retry against a number nothing resolved
    }

    [Fact]
    public async Task Submit_review_404_without_a_head_branch_stays_permanent()
    {
        var handler = new StubHandler(_ =>
            Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        var api = new RestGithubWriteApi(new HttpClient(handler), "pat");

        var review = new PublishReview("o", "r", PrNumber: 27, "APPROVE", "ok", RunId: 1,
            IdempotencyKey: "review:1", HeadBranch: null);
        await Assert.ThrowsAsync<GithubPermanentException>(
            () => api.SubmitReviewAsync(review, CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.PathAndQuery.StartsWith("/repos/o/r/pulls?"));
    }
}
