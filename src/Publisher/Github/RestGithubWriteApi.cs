using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiAgent.Events;
using PiAgent.Platform.GitHub;

namespace PiAgent.Publisher.Github;

/// <summary>
/// The production GitHub write adapter: REST for comments/PRs/reviews, GraphQL for
/// Projects v2 board moves. Failure classification follows docs/publisher.md —
/// 5xx/transport/rate-limit are transient; gone/permission/validation are permanent.
/// </summary>
public sealed class RestGithubWriteApi(HttpClient httpClient, string pat) : IGithubWriteApi
{
    private const string GraphqlUrl = "https://api.github.com/graphql";

    public async Task<long> CreateCommentAsync(PublishComment evt, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post,
            $"repos/{evt.Owner}/{evt.Repo}/issues/{evt.EntityNumber}/comments",
            new JsonObject { ["body"] = evt.Body }, ct);
        return (long)json!["id"]!;
    }

    /// <summary>
    /// Create-or-get: a second round pushes the same workflow.AgentBranch (docs/coder APPEND_SYSTEM,
    /// "a push to the same branch updates the same PR"), so GitHub answers 422 "a pull request
    /// already exists for this head and base". That is not a failure of intent — resolve the open
    /// PR by head and return it. A plain invalid-head 422 (branch truly absent) still fails
    /// permanently. Submission race: two creates for one head resolve to the same PR either way.
    /// Returns the PR's <c>number</c> (the repository-wide number reviews post against),
    /// not the object id.
    /// </summary>
    public async Task<long> CreateOrUpdatePrAsync(PublishPr evt, CancellationToken ct)
    {
        if (evt.Kind is PublishPrKind.Update && evt.PrNumber is { } pr)
        {
            await SendAsync(HttpMethod.Patch, $"repos/{evt.Owner}/{evt.Repo}/pulls/{pr}",
                new JsonObject
                {
                    ["title"] = evt.Title,
                    ["body"] = evt.Body,
                }, ct);
            return pr;
        }

        try
        {
            var created = await SendAsync(HttpMethod.Post, $"repos/{evt.Owner}/{evt.Repo}/pulls",
                new JsonObject
                {
                    ["title"] = evt.Title,
                    ["body"] = evt.Body,
                    ["head"] = evt.HeadBranch,
                    ["base"] = evt.BaseBranch,
                }, ct);
            return (long)created!["number"]!;
        }
        catch (GithubPermanentException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return await FindOpenPrByHeadAsync(evt.Owner, evt.Repo, evt.HeadBranch, ct)
                ?? throw new GithubPermanentException(
                    $"github {evt.Owner}/{evt.Repo}: 422 already-exists but no open PR found for head {evt.HeadBranch}");
        }
    }

    /// <summary>
    /// The review can be scheduled before PrPublished lands (docs/incident-2026-11-26-follow-ups.md
    /// decision 1.2.2), so its PrNumber may be the item number. When the POST 404s and the
    /// event carries the PR head branch, resolve the open PR by head and retry once against
    /// the found number; with no PR found the permanent failure stands. A second 404 (the
    /// resolved PR also missing) propagates — never swallowed.
    /// </summary>
    public async Task<long> SubmitReviewAsync(PublishReview evt, CancellationToken ct)
    {
        try
        {
            return await SubmitReviewOnceAsync(evt.PrNumber, evt, ct);
        }
        catch (GithubPermanentException ex)
            when (evt.HeadBranch is { } head && ex.Message.Contains("[404]", StringComparison.Ordinal))
        {
            if (await FindOpenPrByHeadAsync(evt.Owner, evt.Repo, head, ct) is not { } resolved)
                throw; // no open PR for the head — the permanent failure stands
            return await SubmitReviewOnceAsync(resolved, evt, ct);
        }
    }

    private async Task<long> SubmitReviewOnceAsync(long prNumber, PublishReview evt, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post,
            $"repos/{evt.Owner}/{evt.Repo}/pulls/{prNumber}/reviews",
            new JsonObject
            {
                ["event"] = evt.Verdict,
                ["body"] = evt.Body,
            }, ct);
        return (long)json!["id"]!;
    }

    private async Task<long?> FindOpenPrByHeadAsync(string owner, string repo, string headBranch, CancellationToken ct)
    {
        var existing = await SendAsync(HttpMethod.Get,
            $"repos/{owner}/{repo}/pulls?head={owner}:{headBranch}&state=open", null, ct);
        return existing is JsonArray list && list.Count > 0 && list[0] is { } openPr
            ? (long)openPr!["number"]!
            : null;
    }

    public async Task MoveBoardItemAsync(PublishBoardMove evt, CancellationToken ct)
    {
        // read-before-write: resolve field/option ids by name (the Poller does not sync them)
        var meta = await GraphqlAsync(new JsonObject
        {
            ["query"] = """
                query($owner:String!,$project:Int!,$itemId:ID!) {
                  user(login:$owner) {
                    projectV2(number:$project) {
                      field: projectV2FieldByName(name:"Status") { ... on ProjectV2SingleSelectField { id options { id name } } }
                    }
                  }
                  node(id:$itemId) { id }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["owner"] = evt.Owner,
                ["project"] = evt.ProjectNumber,
                ["itemId"] = evt.ItemNodeId,
            },
        }, ct);

        var field = meta!["data"]!["user"]!["projectV2"]!["field"]!;
        var option = field["options"]!.AsArray().FirstOrDefault(o => o!["name"]!.GetValue<string>() == evt.Status)
            ?? throw new GithubPermanentException($"board column '{evt.Status}' not found");
        if (meta["data"]!["node"]!["id"] is null)
            throw new GithubPermanentException($"board item {evt.ItemNodeId} not found");

        await GraphqlAsync(new JsonObject
        {
            ["query"] = """
                mutation($project:ID!,$item:ID!,$field:ID!,$option:String!) {
                  updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$field,value:{singleSelectOptionId:$option}}) {
                    projectV2Item { id }
                  }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["project"] = await ProjectIdAsync(evt.Owner, evt.ProjectNumber, ct),
                ["item"] = evt.ItemNodeId,
                ["field"] = field["id"]!.GetValue<string>(),
                ["option"] = option!["id"]!.GetValue<string>(),
            },
        }, ct);
    }

    private async Task<string> ProjectIdAsync(string owner, int projectNumber, CancellationToken ct)
    {
        var meta = await GraphqlAsync(new JsonObject
        {
            ["query"] = "query($owner:String!,$project:Int!){ user(login:$owner){ projectV2(number:$project){ id } } }",
            ["variables"] = new JsonObject { ["owner"] = owner, ["project"] = projectNumber },
        }, ct);
        return meta!["data"]!["user"]!["projectV2"]!["id"]!.GetValue<string>()
            ?? throw new GithubPermanentException($"project {owner}#{projectNumber} not found");
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"https://api.github.com/{path}");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
            return JsonNode.Parse(raw) ?? throw new GithubPermanentException($"github returned empty body for {path}");

        throw Classify(response, raw, path);
    }

    private async Task<JsonNode> GraphqlAsync(JsonObject payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphqlUrl);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw Classify(response, raw, "graphql");

        var json = JsonNode.Parse(raw)!;
        if (json["errors"] is { } errors)
            throw new GithubPermanentException($"graphql errors: {errors.ToJsonString()}");
        return json;
    }

    private Exception Classify(HttpResponseMessage response, string raw, string where)
    {
        var detail = GitHubHttp.Redact(raw, pat);
        var status = (int)response.StatusCode;

        if (status is 404 or 422 or 401)
            return new GithubPermanentException($"github {where} [{status}]: {detail}");
        if (status == 403 && GitHubHttp.Remaining(response) == 0)
            return new GithubTransientException(
                $"github {where} [403] rate limit exhausted: {detail}", GitHubHttp.RetryAfter(response));
        if (status == 403 && GitHubHttp.RetryAfter(response) is { } secondary)
            return new GithubTransientException($"github {where} [403] secondary rate limit: {detail}", secondary);
        if (status is 403)
            return new GithubPermanentException($"github {where} [403] forbidden: {detail}");
        if (status is >= 500 or 408 or 429)
            return new GithubTransientException($"github {where} [{status}]: {detail}");

        return new GithubPermanentException($"github {where} [{status}]: {detail}");
    }
}
