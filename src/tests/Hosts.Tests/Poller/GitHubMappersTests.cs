using System.Text.Json;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>GitHub REST payload → snapshot mapping (task 04 detection inputs). Hand-written sample
/// payloads per GitHub's response shapes.</summary>
public class GitHubMappersTests
{
    private const string IssueJson = """
        {
          "node_id": "ISSUE_12", "number": 12, "title": "Fix bug", "body": "steps to reproduce",
          "state": "open", "user": {"login": "alice"},
          "labels": [{"name": "bug"}, {"name": "p1"}],
          "assignees": [{"login": "bob"}, {"login": "carol"}],
          "created_at": "2026-11-01T10:00:00Z", "updated_at": "2026-11-10T09:00:00Z", "closed_at": null,
          "url": "https://api.github.com/repos/o/r/issues/12"
        }
        """;

    private const string PullJson = """
        {
          "node_id": "PR_7", "number": 7, "title": "Add feature", "body": "implements #12",
          "state": "open", "user": {"login": "alice"},
          "labels": [{"name": "review"}], "assignees": [{"login": "bob"}],
          "requested_reviewers": [{"login": "pi-bot"}, {"login": "carol"}],
          "head": {"sha": "head-sha-1"}, "base": {"sha": "base-sha-1"},
          "draft": false, "merged": false, "merged_at": null,
          "created_at": "2026-11-02T10:00:00Z", "updated_at": "2026-11-11T09:00:00Z", "closed_at": null,
          "url": "https://api.github.com/repos/o/r/pulls/7"
        }
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void IsPullRequestPayload_DetectsPullRequestMarker()
    {
        Assert.False(GitHubMappers.IsPullRequestPayload(Parse(IssueJson)));
        Assert.True(GitHubMappers.IsPullRequestPayload(Parse("""
            {"number": 3, "pull_request": {"url": "https://api.github.com/repos/o/r/pulls/3"}}
            """)));
    }

    [Fact]
    public void MapIssue_MapsAllSyncedFields()
    {
        var issue = GitHubMappers.MapIssue(Parse(IssueJson));
        Assert.Equal("ISSUE_12", issue.NodeId);
        Assert.Equal(12, issue.Number);
        Assert.Equal(WorkItemTypes.Issue, issue.Type);
        Assert.Equal("Fix bug", issue.Title);
        Assert.Equal("steps to reproduce", issue.Body);
        Assert.Equal("open", issue.State);
        Assert.Equal("alice", issue.AuthorLogin);
        Assert.Equal(["bob", "carol"], issue.Assignees);
        Assert.Equal(["bug", "p1"], issue.Labels);
        Assert.Equal([], issue.RequestedReviewers);
        Assert.Null(issue.HeadSha);
        Assert.Null(issue.Draft);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), issue.UpdatedAt);
        Assert.Null(issue.ClosedAt);
    }

    [Fact]
    public void MapPullRequest_MapsPrOnlyFields()
    {
        var pr = GitHubMappers.MapPullRequest(Parse(PullJson));
        Assert.Equal(WorkItemTypes.PullRequest, pr.Type);
        Assert.Equal("head-sha-1", pr.HeadSha);
        Assert.Equal("base-sha-1", pr.BaseSha);
        Assert.False(pr.Draft);
        Assert.Null(pr.MergedAt);
        Assert.Equal(["pi-bot", "carol"], pr.RequestedReviewers);
        Assert.Null(pr.ClosedAt);
    }

    [Fact]
    public void MapIssueComment_NumberFromIssueUrl()
    {
        var comment = GitHubMappers.MapIssueComment(Parse("""
            {
              "id": 999, "body": "hello", "user": {"login": "bob"},
              "created_at": "2026-11-01T10:00:00Z", "updated_at": "2026-11-02T10:00:00Z",
              "issue_url": "https://api.github.com/repos/o/r/issues/12"
            }
            """));
        Assert.Equal(999, comment.CommentId);
        Assert.Equal(12, comment.IssueNumber);
        Assert.Equal(GitHubCommentKinds.IssueComment, comment.Kind);
        Assert.Equal("hello", comment.Body);
        Assert.Equal("bob", comment.AuthorLogin);
        Assert.Null(comment.Path);
        Assert.Null(comment.DiffPosition);
    }

    [Fact]
    public void MapReviewComment_MapsThreadFieldsFromPullUrl()
    {
        var comment = GitHubMappers.MapReviewComment(Parse("""
            {
              "id": 555, "body": "nit", "user": {"login": "carol"},
              "created_at": "2026-11-03T10:00:00Z", "updated_at": "2026-11-04T10:00:00Z",
              "path": "src/a.cs", "commit_id": "head-sha-1", "position": 14,
              "in_reply_to_id": 554, "pull_request_review_id": 77,
              "pull_request_url": "https://api.github.com/repos/o/r/pulls/7"
            }
            """));
        Assert.Equal(GitHubCommentKinds.ReviewComment, comment.Kind);
        Assert.Equal(7, comment.IssueNumber);
        Assert.Equal("carol", comment.AuthorLogin);
        Assert.Equal("src/a.cs", comment.Path);
        Assert.Equal("head-sha-1", comment.CommitSha);
        Assert.Equal(14, comment.DiffPosition);
        Assert.Equal(554, comment.InReplyToId);
        Assert.Equal(77, comment.GithubReviewId);
    }

    [Fact]
    public void MapReview_MapsSubmittedReview()
    {
        var review = GitHubMappers.MapReview(Parse("""
            {
              "id": 77, "state": "approved", "body": "lgtm", "user": {"login": "pi-bot"},
              "commit_id": "head-sha-1", "submitted_at": "2026-11-05T10:00:00Z",
              "pull_request_url": "https://api.github.com/repos/o/r/pulls/7"
            }
            """));
        Assert.Equal(77, review.ReviewId);
        Assert.Equal(7, review.PrNumber);
        Assert.Equal("approved", review.State);
        Assert.Equal("pi-bot", review.AuthorLogin);
        Assert.Equal("lgtm", review.Body);
        Assert.Equal("head-sha-1", review.CommitSha);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), review.SubmittedAt);
    }

    [Fact]
    public void NumberFromApiUrl_ParsesTrailingSegment()
    {
        Assert.Equal(12, GitHubMappers.NumberFromApiUrl("https://api.github.com/repos/o/r/issues/12"));
        Assert.Null(GitHubMappers.NumberFromApiUrl("https://api.github.com/repos/o/r/issues/12x"));
        Assert.Null(GitHubMappers.NumberFromApiUrl(null));
    }
}
