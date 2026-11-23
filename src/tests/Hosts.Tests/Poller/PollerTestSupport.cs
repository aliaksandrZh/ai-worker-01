using PiAgent.Poller.Streams;
using PiAgent.Poller.GitHub;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>Shared poller stream test utilities (task 04): queue-based GitHub stub, payload builders,
/// repository seeding, run construction. DB-backed tests use the real-testcontainer PostgresFixture.</summary>
public static class PollerTestSupport
{
    public static readonly PollerOptions TestOptions = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), 100);

    public static Task<Repository> SeedRepositoryAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            GithubRepositoryId = 90210,
            Owner = "octo-org",
            Name = "example",
            FullName = "octo-org/example",
            DefaultBranch = "main",
            IsEnabled = true,
            Configuration = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Repositories.Add(repository);
        db.SaveChanges();
        return Task.FromResult(repository);
    }

    public static StreamRun NewRun(AppDbContext db, Repository repository, HttpMessageHandler handler) => new(
        db, repository,
        new GitHubReadClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, "test-pat"),
        TestOptions);

    /// <summary>Queue-based GitHub list stub: one response per request, defaulting to an empty page.
    /// `Pages` carry the raw array JSON; `NextUrl` emits the Link rel="next" header.</summary>
    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _scheduled = new();
        public readonly List<string> Requests = [];
        public readonly List<string?> IfNoneMatch = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

        public void Enqueue(string arrayJson, string? nextUrl = null, string? etag = null)
        {
            _scheduled.Enqueue(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(arrayJson),
                };
                if (nextUrl is { })
                    response.Headers.Add("Link", $"<{nextUrl}>; rel=\"next\"");
                if (etag is { })
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
                return response;
            });
        }

        public void Enqueue304(string etag = "w-unchanged") =>
            _scheduled.Enqueue(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
                return response;
            });

        public void EnqueueResponse(HttpResponseMessage response) => _scheduled.Enqueue(_ => response);

        public void EnqueueThrow(Exception exception) => _scheduled.Enqueue(_ => throw exception);

        public void Enqueue(HttpStatusCode status, string body = """{"message": "boom"}""") =>
            EnqueueResponse(new HttpResponseMessage(status) { Content = new StringContent(body) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            IfNoneMatch.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            if (_scheduled.Count > 0)
                return Task.FromResult(_scheduled.Dequeue()(request));
            return Task.FromResult(Fallback?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    // ---- GitHub REST payload builders (issue/pull shapes) ----

    public static string QuotedDate(string? value) => value is null ? "null" : $"\"{value}\"";

    public static string LoginsJson(params string[] logins) =>
        "[" + string.Join(",", logins.Select(l => $"{{\"login\": \"{l}\"}}")) + "]";

    public static string LabelsJson(params string[] names) =>
        "[" + string.Join(",", names.Select(n => $"{{\"name\": \"{n}\"}}")) + "]";

    public static string IssueJson(long number, string title, string updated, string state = "open",
        string? closedAt = null, string author = "alice", string authorKey = "alice") =>
        $$"""
        {
          "node_id": "ISSUE_{{number}}", "number": {{number}}, "title": "{{title}}", "body": "body-{{number}}",
          "state": "{{state}}", "user": {"login": "{{author}}"},
          "labels": {{LabelsJson("bug")}}, "assignees": {{LoginsJson("bob")}},
          "created_at": "2026-11-01T08:00:00Z", "updated_at": "{{updated}}", "closed_at": {{QuotedDate(closedAt)}},
          "url": "https://api.github.com/repos/octo-org/example/issues/{{number}}"
        }
        """;

    public static string PullJson(long number, string title, string updated, string state = "open",
        string? headSha = "head-sha-a", string[]? reviewers = null, bool draft = false,
        string? mergedAt = null, string? closedAt = null) =>
        $$"""
        {
          "node_id": "PR_{{number}}", "number": {{number}}, "title": "{{title}}", "body": "pr-body-{{number}}",
          "state": "{{state}}", "user": {"login": "alice"},
          "labels": {{LabelsJson("review")}}, "assignees": {{LoginsJson("bob")}},
          "requested_reviewers": {{LoginsJson(reviewers ?? [])}},
          "head": {"sha": "{{headSha}}"}, "base": {"sha": "base-sha-a"},
          "draft": {{(draft ? "true" : "false")}}, "merged": {{(QuotedDate(mergedAt) == "null" ? "false" : "true")}}, "merged_at": {{QuotedDate(mergedAt)}},
          "created_at": "2026-11-02T08:00:00Z", "updated_at": "{{updated}}", "closed_at": {{QuotedDate(closedAt)}},
          "url": "https://api.github.com/repos/octo-org/example/pulls/{{number}}",
          "pull_request_url": "https://api.github.com/repos/octo-org/example/pulls/{{number}}"
        }
        """;

    public static string ArrayJson(params string[] itemJson) =>
        "[" + string.Join(",", itemJson) + "]";

    public static string ReviewJson(long id, long prNumber, string state, string? body = "lgtm",
        string? submittedAt = null, string author = "pi-bot", string? commitId = "head-sha-a") =>
        $$"""
        {
          "id": {{id}}, "state": "{{state}}", "body": {{QuotedDate(body)}}, "user": {"login": "{{author}}"},
          "commit_id": {{QuotedDate(commitId)}}, "submitted_at": {{QuotedDate(submittedAt)}},
          "pull_request_url": "https://api.github.com/repos/octo-org/example/pulls/{{prNumber}}"
        }
        """;

    public static string IssueCommentJson(long id, long issueNumber, string body, string updated,
        string? author = "bob", string created = "2026-11-18T08:00:00Z") =>
        $$"""
        {
          "id": {{id}}, "body": "{{body}}", "user": {"login": "{{author}}"},
          "created_at": "{{created}}", "updated_at": "{{updated}}",
          "issue_url": "https://api.github.com/repos/octo-org/example/issues/{{issueNumber}}"
        }
        """;

    public static string ReviewCommentJson(long id, long prNumber, string body, string updated,
        string? path = "src/a.cs", string? commitId = "head-sha-a", int? position = 5,
        long? inReplyTo = 554, long? reviewId = 77, string author = "carol",
        string created = "2026-11-18T09:00:00Z") =>
        $$"""
        {
          "id": {{id}}, "body": "{{body}}", "user": {"login": "{{author}}"},
          "created_at": "{{created}}", "updated_at": "{{updated}}",
          "path": {{(path is null ? "null" : $"\"{path}\"")}}, "commit_id": {{(commitId is null ? "null" : $"\"{commitId}\"")}},
          "position": {{position?.ToString() ?? "null"}}, "in_reply_to_id": {{inReplyTo?.ToString() ?? "null"}},
          "pull_request_review_id": {{reviewId?.ToString() ?? "null"}},
          "pull_request_url": "https://api.github.com/repos/octo-org/example/pulls/{{prNumber}}"
        }
        """;

    /// <summary>Minimal GitHubComment row for resolution tests and the deleted-comment sweep's
    /// tombstone tests (task 05).</summary>
    public static GitHubComment SeedComment(AppDbContext db, WorkItem workItem, string kind, long commentId,
        string body = "seeded", string author = "bob", string created = "2026-11-18T08:00:00Z")
    {
        var row = new GitHubComment
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = commentId,
            Kind = kind,
            AuthorLogin = author,
            Body = body,
            CreatedAtGithub = DateTime.Parse(created).ToUniversalTime(),
            UpdatedAtGithub = DateTime.Parse(created).ToUniversalTime(),
            LastSyncedAt = DateTime.UtcNow,
        };
        db.GitHubComments.Add(row);
        db.SaveChanges();
        return row;
    }

    // ---- timeline + introducer payload builders (task 05) ----

    public static string TimelineJson(params string[] events) => ArrayJson(events);

    public static string CrossReferencedJson(long id, long sourceNumber, bool sourceIsPr = true,
        string repo = "octo-org/example", string created = "2026-11-19T12:00:00Z")
    {
        var repositoryUrl = $"https://api.github.com/repos/{repo}";
        var marker = sourceIsPr
            ? $"\"pull_request\": {{\"url\": \"https://api.github.com/repos/{repo}/pulls/{sourceNumber}\"}}, "
            : "";
        var source = $"{{\"issue\": {{\"number\": {sourceNumber}, \"repository_url\": \"{repositoryUrl}\", {marker}\"title\": \"source item\"}}}}";
        return $$"""
        {
          "id": {{id}}, "event": "cross-referenced", "created_at": "{{created}}",
          "source": {{source}}
        }
        """;
    }

    public static string ClosedTimelineJson(long id, string? commitId = null, string? commitMessage = null,
        string created = "2026-11-19T13:00:00Z") =>
        $$"""
        {
          "id": {{id}}, "event": "closed", "created_at": "{{created}}",
          "commit_id": {{QuotedDate(commitId)}}, "commit_message": {{QuotedDate(commitMessage)}}
        }
        """;

    public static string ReviewRequestTimelineJson(long id, bool removed, string created = "2026-11-19T14:00:00Z",
        string? reviewer = "pi-bot", string? team = null)
    {
        var reviewerJson = reviewer is null ? "null" : $"{{\"login\": \"{reviewer}\"}}";
        var teamJson = team is null ? "null" : $"{{\"name\": \"{team}\"}}";
        var eventType = removed ? "review_request_removed" : "review_requested";
        return $$"""
        {
          "id": {{id}}, "event": "{{eventType}}", "created_at": "{{created}}",
          "requested_reviewer": {{reviewerJson}}, "requested_team": {{teamJson}}
        }
        """;
    }

    public static string TimelineEventJson(long id, string eventType, string created = "2026-11-19T15:00:00Z") =>
        $$"""
        {"id": {{id}}, "event": "{{eventType}}", "created_at": "{{created}}"}
        """;

    public static string IntroducersJson(params (long Number, string? MergedAt)[] pulls) =>
        ArrayJson(pulls.Select(p =>
            $"{{\"number\": {p.Number}, \"state\": \"closed\", \"title\": \"pr {p.Number}\", \"merged_at\": {QuotedDate(p.MergedAt)}}}")
            .ToArray());

    /// <summary>Minimal WorkItem row for resolution tests (the streams never create one).</summary>
    public static WorkItem SeedWorkItem(AppDbContext db, Repository repository, long number, string type,
        string updatedAt = "2026-11-10T08:00:00Z")
    {
        var row = new WorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = repository.Id,
            GithubNodeId = $"{type.ToUpperInvariant()}_{number}",
            Number = number,
            Type = type,
            Title = $"work item {number}",
            Body = "seeded",
            State = "open",
            AuthorLogin = "alice",
            Assignees = "[]",
            Labels = "[]",
            CreatedAtGithub = DateTime.Parse(updatedAt).ToUniversalTime(),
            UpdatedAtGithub = DateTime.Parse(updatedAt).ToUniversalTime(),
            LastSyncedAt = DateTime.UtcNow,
        };
        db.WorkItems.Add(row);
        db.SaveChanges();
        return row;
    }
}
