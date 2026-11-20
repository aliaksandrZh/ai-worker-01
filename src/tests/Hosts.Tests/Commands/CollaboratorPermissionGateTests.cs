using System.Net;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Orchestrator.Commands;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// Collaborator permission gate per decision 66 (02 §21): role_name/permission mapping, 404 →
/// UserNotFound, 401/non-rate-limit 403 → SystemConfigError, rate-limits/5xx/network → Transient,
/// ≤60 s cache; plus the repositories.yaml allowlist evaluation (decision 49).
/// </summary>
public class CollaboratorPermissionGateTests
{
    private static CollaboratorPermissionGate Gate(HttpMessageHandler handler, TimeSpan? cacheTtl = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return new CollaboratorPermissionGate(httpClient, "test-pat", cacheTtl);
    }

    private static void EnqueueJson(PollerTestSupport.StubHandler handler, string json)
    {
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private static void EnqueuePermission(PollerTestSupport.StubHandler handler, string permission, string? roleName = null)
    {
        var json = roleName is null
            ? $$"""{"permission": "{{permission}}", "role_name": "{{permission}}"}"""
            : $$"""{"permission": "{{permission}}", "role_name": "{{roleName}}"}""";
        EnqueueJson(handler, json);
    }

    // ---- decision 66 role mapping ----

    [Theory]
    [InlineData("admin")]
    [InlineData("maintain")]
    [InlineData("write")]
    public async Task RoleNameAtOrAboveWrite_Allowed(string roleName)
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "admin", roleName);
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Allowed, outcome.Kind);
    }

    [Fact]
    public async Task PermissionFieldFallback_NoRoleName()
    {
        var handler = new PollerTestSupport.StubHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "write"}"""),
        });
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Allowed, outcome.Kind);
    }

    [Fact]
    public async Task PermissionNone_Rejected_InsufficientPermission()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "none", "none");
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.InsufficientPermission, outcome.Kind);
    }

    [Fact]
    public async Task RoleNameTriage_Rejected_InsufficientPermission()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "read", "triage");
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.InsufficientPermission, outcome.Kind);
    }

    [Fact]
    public async Task RoleNameRead_Rejected_InsufficientPermission()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "read", "read");
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.InsufficientPermission, outcome.Kind);
    }

    [Fact]
    public async Task MinimumPermissionHonored()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "read", "triage");
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", "read", CancellationToken.None);
        Assert.Equal(PermissionKind.Allowed, outcome.Kind);
    }

    [Fact]
    public async Task UnknownConfiguredMinimum_FallsBackToWrite()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "read", "read");
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", "boss", CancellationToken.None);
        Assert.Equal(PermissionKind.InsufficientPermission, outcome.Kind);
    }

    // ---- error classification (02 §21/decision 66) ----

    [Fact]
    public async Task NotFound404_UserNotFound()
    {
        var handler = new PollerTestSupport.StubHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var outcome = await Gate(handler).CheckAsync("org", "repo", "ghost", null, CancellationToken.None);
        Assert.Equal(PermissionKind.UserNotFound, outcome.Kind);
    }

    [Fact]
    public async Task PrimaryRateLimit403_Transient_WithRetryAfter()
    {
        var handler = new PollerTestSupport.StubHandler();
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("Retry-After", "30");
        handler.EnqueueResponse(response);
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Transient, outcome.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), outcome.RetryAfter);
    }

    [Fact]
    public async Task Secondary429_Transient()
    {
        var handler = new PollerTestSupport.StubHandler();
        var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
        response.Headers.Add("Retry-After", "12");
        handler.EnqueueResponse(response);
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Transient, outcome.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), outcome.RetryAfter);
    }

    [Fact]
    public async Task Unauthorized401_SystemConfigError()
    {
        var handler = new PollerTestSupport.StubHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.SystemConfigError, outcome.Kind);
    }

    [Fact]
    public async Task Regular403_SystemConfigError()
    {
        var handler = new PollerTestSupport.StubHandler();
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
        response.Headers.Add("X-RateLimit-Remaining", "4990");
        handler.EnqueueResponse(response);
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.SystemConfigError, outcome.Kind);
    }

    [Fact]
    public async Task ServerError500_Transient()
    {
        var handler = new PollerTestSupport.StubHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
        var outcome = await Gate(handler).CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Transient, outcome.Kind);
    }

    [Fact]
    public async Task NetworkFailure_Transient()
    {
        var gate = Gate(new ThrowingHandler(), null);
        var outcome = await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Transient, outcome.Kind);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    // ---- cache (decision 66: ≤60 s, absolute) ----

    [Fact]
    public async Task CacheWithinTtl_SecondCheckSkipsHttp()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "admin", "admin");
        var gate = Gate(handler);
        await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        var outcome = await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Allowed, outcome.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CacheExpired_Rechecks()
    {
        var handler = new PollerTestSupport.StubHandler();
        EnqueuePermission(handler, "admin", "admin");
        EnqueuePermission(handler, "admin", "admin");
        var gate = Gate(handler, cacheTtl: TimeSpan.FromMilliseconds(1));
        await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        await Task.Delay(5);
        await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TransientOutcomes_AreNotCached()
    {
        var handler = new PollerTestSupport.StubHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        EnqueuePermission(handler, "admin", "admin");
        var gate = Gate(handler);
        await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        var outcome = await gate.CheckAsync("org", "repo", "alice", null, CancellationToken.None);
        Assert.Equal(PermissionKind.Allowed, outcome.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- allowlist (decision 49, Repository.Configuration jsonb) ----

    private const string ConfigJson = """{"allowlist": {"enabled": true, "logins": ["alice"]}, "permitted_commands": ["plan", "status"], "permitted_work_item_types": ["issue"], "authorization": {"minimum_permission": "write"}}""";

    [Fact]
    public void AllowlistEnabled_MemberAllowed()
    {
        Assert.Equal(AllowlistVerdict.Allowed, CollaboratorPermissionGate.EvaluateAllowlist(ConfigJson, "Alice"));
    }

    [Fact]
    public void AllowlistEnabled_NonMemberDenied()
    {
        Assert.Equal(AllowlistVerdict.Denied, CollaboratorPermissionGate.EvaluateAllowlist(ConfigJson, "bob"));
    }

    [Fact]
    public void EmptiedAllowlist_WriteBaselineAllowed()
    {
        var emptied = """{"allowlist": {"enabled": false, "logins": []}, "authorization": {"minimum_permission": "write"}}""";
        Assert.Equal(AllowlistVerdict.Allowed, CollaboratorPermissionGate.EvaluateAllowlist(emptied, "bob"));
    }

    [Fact]
    public void MalformedConfigJson_Denied()
    {
        Assert.Equal(AllowlistVerdict.Denied, CollaboratorPermissionGate.EvaluateAllowlist("{not json", "alice"));
    }
}
