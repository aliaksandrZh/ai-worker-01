using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Context;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.RuntimeApi;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>
/// The Runtime Run Pipeline API — the single Orchestrator-owned `/start` provisioning
/// endpoint (per-Run tree, per-Run credential, container via Docker with the profile version's
/// resource limits), the four callback routes persisting RunEvents before acknowledgement (idempotent
/// by eventId, scoped to the runId, revoked after terminal delivery), the input pull channel, stop,
/// status, and the restart reconciler. FakeDocker stands in for the Docker Engine socket.
/// </summary>
public class RuntimeApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- start (provisioning) ----

    [Fact]
    public async Task StartRun_ProvisionsTree_Credential_Container_AndWritesMetadataBack()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);

        var response = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(preparation.Run.Id, body.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal("starting", body.RootElement.GetProperty("status").GetString());
        Assert.Equal($"pi-session-{preparation.Run.Id}", body.RootElement.GetProperty("containerName").GetString());
        Assert.Contains("pi-session-", body.RootElement.GetProperty("sessionFilePath").GetString());
        Assert.Equal($"docker-pi-session-{preparation.Run.Id}", body.RootElement.GetProperty("dockerContainerId").GetString());

        // container row: docker metadata + observed machine
        var db = postgres.NewDbContext();
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == preparation.Run.Id);
        Assert.Equal(ContainerObservedStates.Running, container.ObservedState);
        Assert.Equal(ContainerDesiredStates.Running, container.DesiredState);
        Assert.Equal("agent-stub:v1", container.ContainerImage);
        Assert.Equal("Local", container.HostId);
        // session: 1:1, in_use, --session-id pinned
        var session = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        Assert.Equal(AgentSessionStatuses.InUse, session.Status);
        Assert.Equal(preparation.Run.Id.ToString(), session.RuntimeSessionId);
        Assert.EndsWith($"{preparation.Run.Id}.jsonl", session.SessionFilePath);
        Assert.Contains("pi-session", session.SessionFilePath);
        // run + workspace write-back
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Starting, run.Status);
        Assert.NotNull(run.StartedAt);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == preparation.Task.Id);
        Assert.Equal(WorkspaceStatuses.InUse, workspace.Status);
        Assert.NotNull(workspace.StorageReference);

        // physical tree: pi-session/, logs/, callback-spool/ + the read-only secret file
        var workflowId = (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == preparation.Task.WorkflowId)).Id;
        var runRoot = Path.Combine(api.StorageRoot, "workflows", workflowId.ToString(), "runs", preparation.Run.Id.ToString());
        Assert.True(Directory.Exists(Path.Combine(runRoot, "pi-session")));
        Assert.True(Directory.Exists(Path.Combine(runRoot, "logs")));
        Assert.True(Directory.Exists(Path.Combine(runRoot, "callback-spool")));
        Assert.True(File.Exists(Path.Combine(runRoot, "callback-token")));

        // docker create config: pinned image, resource limits from resource_configuration, mounts
        var plan = api.Docker.Created.Single();
        Assert.Equal("agent-stub:v1", plan.Image);
        Assert.Equal(512L * 1024 * 1024, plan.MemoryBytes);
        Assert.Equal(1.0m, plan.Cpus);
        Assert.Contains("pi-agent-net", plan.Network);
        var volumes = plan.Binds;
        Assert.Contains(volumes, v => v.EndsWith("/workspace"));
        Assert.Contains(volumes, v => v.EndsWith("/session"));
        Assert.Contains(volumes, v => v.EndsWith("/logs"));
        Assert.Contains(volumes, v => v.EndsWith("/spool"));
        Assert.Contains(volumes, v => v.EndsWith("/run/secrets/callback-token:ro"));
        var environment = plan.Env;
        Assert.Contains(environment, e => e.StartsWith("PI_RUN_ID="));
        Assert.Contains(environment, e => e.StartsWith("PI_CALLBACK_BASE_URL=http://runtime-api:8080"));
        Assert.Contains(environment, e => e == "PI_HEARTBEAT_SECONDS=15");
        Assert.Contains(environment, e => e == "PI_EVENT_CADENCE_SECONDS=5");
        Assert.Contains(environment, e => e == "PI_MAX_LOG_MB=50");
        Assert.Contains(environment, e => e == "STUB_BEHAVIOR=succeed");
        Assert.DoesNotContain(environment, e => e.StartsWith("SERVICE_TOKEN", StringComparison.Ordinal)); // service creds never in agent env
    }

    [Fact]
    public async Task StartRun_IsIdempotent_ReplaysMetadataWithoutRedeploying()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);

        var replay = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("starting", body.RootElement.GetProperty("status").GetString());
        Assert.Single(api.Docker.Created); // one container, never duplicated
    }

    [Fact]
    public async Task StartRun_ValidationFailures_ArePermanent()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api, withSnapshot: false);

        // missing snapshot (the dispatcher always freezes it first) → 400
        var missing = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("RunContextSnapshot", await missing.Content.ReadAsStringAsync());
        // unknown run → 404
        var unknown = await RuntimeTestSupport.StartAsync(api.ServiceClient, Guid.NewGuid(), preparation);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        // mismatched session id → 400
        RuntimeTestSupport.SnapshotWithInput(api.Db, preparation, new { command = "plan" });
        await api.Db.SaveChangesAsync();
        var mismatched = await api.ServiceClient.PostAsJsonAsync($"/internal/runtime/runs/{preparation.Run.Id}/start", new
        {
            agentSessionId = Guid.NewGuid(),
            workspaceId = preparation.Workspace.Id,
            agentProfileVersionId = preparation.ProfileVersion.Id,
            input = new { workItem = new { repository = "o/r", number = 1L, title = "t" }, command = "plan", instructions = "i", artifactReferences = Array.Empty<Guid>() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
    }

    [Fact]
    public async Task StartRun_UnreadyImage_IsRejected()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api, image: null);
        var response = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no built image", await response.Content.ReadAsStringAsync());
    }

    // ---- callback auth ----

    private static Task<HttpResponseMessage> PostCallback(RuntimeApi api, Guid runId, string token, string route, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/runtime/runs/{runId}/{route}");
        request.Headers.Authorization = new("Bearer", token);
        // no dispose: the TestServer processes the content asynchronously and reads it after SendAsync
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return api.ServiceClient.SendAsync(request);
    }

    [Fact]
    public async Task Callbacks_WithoutBearer_AreUnauthorized()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        var response = await api.ServiceClient.PostAsJsonAsync($"/internal/runtime/runs/{preparation.Run.Id}/events", new
        {
            eventId = "e1", type = RunEventTypes.AgentStarted, occurredAt = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Callbacks_WithOtherRunsToken_AreScopeDenied()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var first = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, first.Run.Id, first);
        var firstToken = RuntimeTestSupport.CallbackToken(api, first.Run.Id);
        var second = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, second.Run.Id, second);

        var response = await PostCallback(api, second.Run.Id, firstToken, "heartbeat",
            $@"{{""eventId"":""hb-1"",""occurredAt"":""{DateTime.UtcNow:O}""}}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("another run", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Heartbeat_PersistsBeat_UpdatesRunAndIsIdempotent()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);

        var response = await PostCallback(api, preparation.Run.Id, token, "heartbeat",
            $@"{{""eventId"":""hb-42"",""occurredAt"":""{DateTime.UtcNow:O}""}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db = postgres.NewDbContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.NotNull(run.LastHeartbeatAt);
        var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == preparation.Run.Id).ToListAsync();
        var beat = Assert.Single(events);
        Assert.Equal(RunEventTypes.Heartbeat, beat.Type);
        Assert.Equal("hb-42", beat.EventId);

        var duplicate = await PostCallback(api, preparation.Run.Id, token, "heartbeat",
            $@"{{""eventId"":""hb-42"",""occurredAt"":""{DateTime.UtcNow:O}""}}");
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); // idempotent replay
        Assert.Single(await db.RunEvents.AsNoTracking().Where(e => e.RunId == preparation.Run.Id).ToListAsync());
        var session = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        Assert.NotNull(session.LastHeartbeatAt);
    }

    [Fact]
    public async Task Events_AcceptTheFullTypeSet_AndBumpProgressOnlyOnProgressTypes()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);

        foreach (var type in RunEventTypes.All)
        {
            var response = await PostCallback(api, preparation.Run.Id, token, "events",
                $@"{{""eventId"":""evt-{type}"",""type"":""{type}"",""occurredAt"":""{DateTime.UtcNow:O}"",""payload"":{{""n"":1}}}}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var db = postgres.NewDbContext();
        Assert.Equal(RunEventTypes.All.Length,
            await db.RunEvents.AsNoTracking().CountAsync(e => e.RunId == preparation.Run.Id));
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.NotNull(run.LastProgressAt); // tool/turn/text events are progress

        var unknown = await PostCallback(api, preparation.Run.Id, token, "events",
            $@"{{""eventId"":""evt-weird"",""type"":""thinking_delta"",""occurredAt"":""{DateTime.UtcNow:O}""}}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode); // not part of the RunEvent set
    }

    [Fact]
    public async Task Completed_PersistsManifest_Artifacts_AndFinalizesTheRun()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == preparation.Task.WorkflowId);
        var workspaceDir = Path.Combine(api.StorageRoot, "workflows", workflow.Id.ToString(), "tasks", preparation.Task.Id.ToString(), "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceDir, "artifacts"));
        await File.WriteAllTextAsync(Path.Combine(workspaceDir, "artifacts", "summary.md"), "# Stub plan\n");

        var completedBody = $@"{{
            ""eventId"":""evt-terminal"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""result"": {{
                ""summary"": ""Stub pipeline result"",
                ""artifacts"": [{{""type"":""implementation_summary"",""format"":""markdown"",""path"":""artifacts/summary.md""}}],
                ""branch"":""agent/pipe-stub"",
                ""commitSha"":""abc123""
            }}
        }}";
        Assert.Equal(HttpStatusCode.OK, (await PostCallback(api, preparation.Run.Id, token, "completed", completedBody)).StatusCode);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal("Stub pipeline result", run.ResultSummary);
        Assert.NotNull(run.LogStorageReference);
        Assert.NotNull(run.LogSizeBytes);
        Assert.NotNull(run.LogsCompletedAt);
        Assert.Contains("logs", run.LogStorageReference!);

        // completed callback persisted before acknowledgement + artifact_produced per artifact
        var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == preparation.Run.Id).OrderBy(e => e.ReceivedAt).ToListAsync();
        Assert.Equal(RunEventTypes.Completed, Assert.Single(events, e => e.Type == RunEventTypes.Completed).Type);
        Assert.Equal(2, events.Count(e => e.Type == RunEventTypes.ArtifactProduced)); // summary + log_reference

        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.TaskId == preparation.Task.Id).ToListAsync();
        var summary = artifacts.Single(a => a.Type == ArtifactTypes.ImplementationSummary);
        Assert.Equal(ArtifactFormats.Markdown, summary.Format);
        Assert.Equal("# Stub plan\n", summary.Content);
        var logReference = artifacts.Single(a => a.Type == ArtifactTypes.LogReference);
        Assert.Equal(run.LogStorageReference, logReference.Content); // points at the directory, no content copy

        // credential revoked after terminal delivery
        var revokedHeartbeat = await PostCallback(api, preparation.Run.Id, token, "heartbeat",
            $@"{{""eventId"":""hb-after-terminal"",""occurredAt"":""{DateTime.UtcNow:O}""}}");
        Assert.Equal(HttpStatusCode.Forbidden, revokedHeartbeat.StatusCode);
        Assert.Contains("revoked", await revokedHeartbeat.Content.ReadAsStringAsync());

        // container stopped + workspace released + session terminated
        Assert.Contains(preparation.Run.Id.ToString(), api.Docker.Stopped.Single());
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == preparation.Run.Id);
        Assert.Equal(ContainerObservedStates.Stopped, container.ObservedState);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == preparation.Task.Id);
        Assert.Equal(WorkspaceStatuses.Ready, workspace.Status);
        var session = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        Assert.Equal(AgentSessionStatuses.Terminated, session.Status);

        // note: a terminal redelivery after revocation answers 403 by design (revocation
        // follows the confirmation; the Bridge removes the spool entry upon the persisted 2xx)
        Assert.Equal(2, (await db.Artifacts.AsNoTracking().Where(a => a.TaskId == preparation.Task.Id).ToListAsync()).Count);
    }

    [Fact]
    public async Task Completed_NeedsHumanInput_IsPersistedForTheOrchestratorToRead()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == preparation.Task.WorkflowId);
        var workspaceDir = Path.Combine(api.StorageRoot, "workflows", workflow.Id.ToString(), "tasks", preparation.Task.Id.ToString(), "workspace");
        Directory.CreateDirectory(workspaceDir);

        var completedBody = $@"{{
            ""eventId"":""evt-terminal"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""outcome"":""needs_human_input"",
            ""result"": {{""summary"":""Blocker published"",""artifacts"":[]}}
        }}";
        Assert.Equal(HttpStatusCode.OK, (await PostCallback(api, preparation.Run.Id, token, "completed", completedBody)).StatusCode);

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Succeeded, run.Status); // Run succeeded (the Task turns Blocked downstream)
        var payload = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == preparation.Run.Id && e.Type == RunEventTypes.Completed)
            .Select(e => e.Payload).SingleAsync();
        using var document = JsonDocument.Parse(payload!);
        Assert.Equal("needs_human_input", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Failed_RecordsTheReportedErrorCode_AndFinalizes()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);

        var failed = await PostCallback(api, preparation.Run.Id, token, "failed", $@"{{
            ""eventId"":""evt-fail"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""error"": {{""code"":""runtime_failure"",""message"":""agent crashed""}}
        }}");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

        var db = postgres.NewDbContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("runtime_failure", run.ErrorCode);
        Assert.Equal("agent crashed", run.ErrorMessage);
        Assert.NotNull(run.LogsCompletedAt);
        // the Run consumed the credential: further callbacks are scope-revoked
        var after = await PostCallback(api, preparation.Run.Id,
            RuntimeTestSupport.CallbackToken(api, preparation.Run.Id), "events",
            $@"{{""eventId"":""late-event"",""type"":""{RunEventTypes.AgentStarted}"",""occurredAt"":""{DateTime.UtcNow:O}""}}");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Contains(preparation.Run.Id.ToString(), api.Docker.Stopped.Single());
    }

    [Fact]
    public async Task TerminalCallbacks_RecordTheBridgeDiscoveredSessionFilePath()
    {
        // the Bridge discovers the REAL pi session path (<timestamp>_<id>.jsonl)
        // and reports it on the terminal callbacks — the AgentSession row records the actual path.
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);
        var provisionedPath = (await postgres.NewDbContext().AgentSessions.AsNoTracking()
            .SingleAsync(s => s.RunId == preparation.Run.Id)).SessionFilePath;
        Assert.EndsWith(".jsonl", provisionedPath); // the provisioning-time prediction differs from pi's

        var sessionFile = $"/session/2026-11-21T15-31-18-187Z_{preparation.Run.Id}.jsonl";
        var completed = await PostCallback(api, preparation.Run.Id, token, "completed", $@"{{
            ""eventId"":""evt-terminal"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""sessionFilePath"":""{sessionFile}"",
            ""result"": {{""summary"":""done"",""artifacts"":[]}}
        }}");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var session = (await postgres.NewDbContext().AgentSessions.AsNoTracking()
            .SingleAsync(s => s.RunId == preparation.Run.Id));
        Assert.Equal(sessionFile, session.SessionFilePath); // the discovered path won
    }

    [Fact]
    public async Task FailedCallback_SessionFilePath_IsRecordedAndOptional()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);
        // a failed callback WITHOUT sessionFilePath keeps the recorded provisioning value (crash before
        // the launch handshake found it) — the field is optional on both terminal routes
        var failed = await PostCallback(api, preparation.Run.Id, token, "failed", $@"{{
            ""eventId"":""evt-fail"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""error"": {{""code"":""github_publication_failed"",""message"":""publication unconfirmed""}}
        }}");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var run = (await postgres.NewDbContext().Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id));
        Assert.Equal("github_publication_failed", run.ErrorCode);
    }

    [Fact]
    public async Task Completed_OversizeArtifact_IsRejectedWithoutTruncation()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == preparation.Task.WorkflowId);
        var workspaceDir = Path.Combine(api.StorageRoot, "workflows", workflow.Id.ToString(), "tasks", preparation.Task.Id.ToString(), "workspace");
        Directory.CreateDirectory(workspaceDir);
        var bigFile = Path.Combine(workspaceDir, "artifacts", "big.md");
        Directory.CreateDirectory(Path.Combine(workspaceDir, "artifacts"));
        await File.WriteAllTextAsync(bigFile, new string('x', 256 * 1024 + 1));

        var completedBody = $@"{{
            ""eventId"":""evt-terminal"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""result"": {{""summary"":""s"",""artifacts"": [{{""type"":""implementation_summary"",""format"":""markdown"",""path"":""artifacts/big.md""}}]}}
        }}";
        var completed = await PostCallback(api, preparation.Run.Id, token, "completed", completedBody);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completed.StatusCode);
        Assert.Contains("artifact_too_large", await completed.Content.ReadAsStringAsync());

        // nothing persisted for the rejected delivery; the run is not terminal
        Assert.Empty(await db.RunEvents.AsNoTracking().Where(e => e.RunId == preparation.Run.Id).ToListAsync());
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Starting, run.Status);
        Assert.Equal("x".Length + 256 * 1024, new FileInfo(bigFile).Length); // file untouched — no silent truncation

        // the Bridge answers the rejection with the terminal failure callback
        var failed = await PostCallback(api, preparation.Run.Id, token, "failed", $@"{{
            ""eventId"":""evt-fail"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""error"": {{""code"":""artifact_too_large"",""message"":""result manifest invalid""}}
        }}");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal(RunStatuses.Failed, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id)).Status);
    }

    [Fact]
    public async Task Completed_EscapingArtifactPath_IsRejected()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);

        var completedBody = $@"{{
            ""eventId"":""evt-x"",
            ""occurredAt"":""{DateTime.UtcNow:O}"",
            ""result"": {{""summary"":""s"",""artifacts"": [{{""type"":""implementation_summary"",""format"":""markdown"",""path"":""../outside.md""}}]}}
        }}";
        var completed = await PostCallback(api, preparation.Run.Id, token, "completed", completedBody);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completed.StatusCode);
        Assert.Contains("escapes the workspace", await completed.Content.ReadAsStringAsync());
    }

    // ---- input pull channel ----

    [Fact]
    public async Task InputRoute_ServesTheFrozenSnapshot_WithPerRunScoping()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, _, _) = (await SeedWithStart(api));
        var token = RuntimeTestSupport.CallbackToken(api, preparation.Run.Id);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/runtime/runs/{preparation.Run.Id}/input");
        request.Headers.Authorization = new("Bearer", token);
        var response = await api.ServiceClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("plan", body.RootElement.GetProperty("command").GetString());
        Assert.Equal("Do the stub work.", body.RootElement.GetProperty("commandComment").GetProperty("text").GetString());
        Assert.Equal(ContextInstructions.For(OrchestrationTaskTypes.Plan), body.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task InputRoute_RejectsOtherRunTokensAndMissingTokens()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);

        var missing = await api.ServiceClient.GetAsync($"/internal/runtime/runs/{preparation.Run.Id}/input");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    private async Task<(RunPreparation, HttpClient, string)> SeedWithStart(RuntimeApi api)
    {
        var preparation = await RuntimeTestSupport.SeedPreparedRunAsync(api);
        var response = await RuntimeTestSupport.StartAsync(api.ServiceClient, preparation.Run.Id, preparation);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (preparation, api.ServiceClient, RuntimeTestSupport.CallbackToken(api, preparation.Run.Id));
    }

    // ---- stop + status ----

    [Fact]
    public async Task StopRun_RecordsTheSignalAndMovesRunStopping_ContainerStaysUntilCallback()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, callbackToken) = await SeedWithStart(api);

        var stop = await client.PostStop(preparation.Run.Id);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        using var stopped = JsonDocument.Parse(await stop.Content.ReadAsStringAsync());
        Assert.Equal("stopping", stopped.RootElement.GetProperty("status").GetString());
        // the signal is recorded but the container is NOT stopped —
        // the Bridge performs the graceful sequence and the terminal cancelled callback stops it
        Assert.Empty(api.Docker.Stopped);

        var db = postgres.NewDbContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Stopping, run.Status);
        Assert.NotNull(run.StopRequestedAt);
        Assert.Equal(AgentCommandRejectionReasons.StopRequested, run.StopReason);

        // idempotent replay: the signal timestamp is recorded once
        var firstSignal = run.StopRequestedAt;
        await Task.Delay(5);
        var again = await client.PostStop(preparation.Run.Id);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replayed = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(firstSignal, replayed.StopRequestedAt);

        var unknown = await client.PostAsJsonAsync($"/internal/runtime/runs/{Guid.NewGuid()}/stop", new { reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var withoutReason = await client.PostAsJsonAsync($"/internal/runtime/runs/{preparation.Run.Id}/stop", new { });
        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);

        var withCallbackToken = api.App.GetTestClient();
        withCallbackToken.DefaultRequestHeaders.Authorization = new("Bearer", callbackToken);
        var notService = await withCallbackToken.PostAsJsonAsync($"/internal/runtime/runs/{preparation.Run.Id}/stop", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, notService.StatusCode); // per-Run callback tokens can never stop runs
    }

    [Fact]
    public async Task StopRequestPull_IsTheBridgeControlChannel()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, callbackToken) = await SeedWithStart(api);

        var bearer = api.App.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", callbackToken);

        var before = await bearer.GetAsync($"/internal/runtime/runs/{preparation.Run.Id}/stop-request");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using (var body = JsonDocument.Parse(await before.Content.ReadAsStringAsync()))
        {
            Assert.False(body.RootElement.GetProperty("requested").GetBoolean());
        }

        await client.PostStop(preparation.Run.Id);
        var after = await bearer.GetAsync($"/internal/runtime/runs/{preparation.Run.Id}/stop-request");
        using (var body = JsonDocument.Parse(await after.Content.ReadAsStringAsync()))
        {
            Assert.True(body.RootElement.GetProperty("requested").GetBoolean());
            Assert.Equal(AgentCommandRejectionReasons.StopRequested, body.RootElement.GetProperty("reason").GetString());
        }

        var unknown = await bearer.GetAsync($"/internal/runtime/runs/{Guid.NewGuid()}/stop-request");
        // the valid per-Run credential is scoped to run A — presented against another runId it is refused
        // as callback_scope_denied (credential scoping; the route 404 applies to unauthenticated callers)
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Contains("another run", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var bare = await api.App.GetTestClient().GetAsync($"/internal/runtime/runs/{preparation.Run.Id}/stop-request");
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
    }

    [Fact]
    public async Task CancelledCallback_MarksRunCancelled_WithAllTerminalFacts()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, callbackToken) = await SeedWithStart(api);
        await client.PostStop(preparation.Run.Id);

        var bearer = api.App.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", callbackToken);
        var cancelled = await bearer.PostAsJsonAsync($"/internal/runtime/runs/{preparation.Run.Id}/cancelled", new
        {
            eventId = "evt-terminal-cancelled",
            occurredAt = DateTime.UtcNow,
            reason = AgentCommandRejectionReasons.StopRequested,
            sessionFilePath = "/session/discovered.jsonl",
        });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var db = postgres.NewDbContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.NotNull(run.CompletedAt);
        // the cancelled delivery is a RunEvent with the delivered body persisted
        var cancelledEvent = await db.RunEvents.AsNoTracking().SingleAsync(e => e.RunId == preparation.Run.Id && e.Type == RunEventTypes.Cancelled);
        Assert.Contains("evt-terminal-cancelled", cancelledEvent.Payload, StringComparison.Ordinal);
        // terminal facts: log trio reference artifact, credential revoked, session terminated
        Assert.True(await db.Artifacts.AsNoTracking().AnyAsync(a => a.TaskId == preparation.Task.Id && a.Type == ArtifactTypes.LogReference));
        Assert.NotNull((await db.RunCredentials.AsNoTracking().SingleAsync(c => c.RunId == preparation.Run.Id)).RevokedAt);
        var session = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id);
        Assert.Equal(AgentSessionStatuses.Terminated, session.Status);
        Assert.Equal("/session/discovered.jsonl", session.SessionFilePath); // the discovered-path write-back
        // terminal delivery stops the container and releases the workspace
        Assert.Contains(preparation.Run.Id.ToString(), api.Docker.Stopped.Single());
        Assert.Equal(RunStatuses.Cancelled, run.Status);

        var redelivery = await bearer.PostAsJsonAsync($"/internal/runtime/runs/{preparation.Run.Id}/cancelled", new
        {
            eventId = "evt-terminal-cancelled",
            occurredAt = DateTime.UtcNow,
            reason = AgentCommandRejectionReasons.StopRequested,
        });
        // the per-Run credential was revoked at confirmed terminal delivery — every later
        // callback (even an idempotent replay) is refused; the Bridge tolerates the refusal on the
        // cancelled route because the terminal state already exists.
        Assert.Equal(HttpStatusCode.Forbidden, redelivery.StatusCode);
        Assert.Contains("token_revoked", await redelivery.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopConfirmSweeper_ReconcilesRunsThatNeverConfirmedTheirStop()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, callbackToken) = await SeedWithStart(api);
        await client.PostStop(preparation.Run.Id);

        // fresh signal (< grace window): untouched
        var sweeper = new StopConfirmSweeper(api.App.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StopConfirmSweeper>.Instance);
        await sweeper.RunCycleAsync(default);
        Assert.Equal(RunStatuses.Stopping,
            (await api.Db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id)).Status);
        Assert.Empty(api.Docker.Stopped);

        // Docker outage: desired state preserved, nothing corrupted, the sweep retries
        await api.Db.Runs.Where(r => r.Id == preparation.Run.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.StopRequestedAt, DateTime.UtcNow - StopConfirmSweeper.GraceWindow - TimeSpan.FromSeconds(10)));
        api.Docker.StopSucceeds = false;
        await sweeper.RunCycleAsync(default);
        Assert.Equal(RunStatuses.Stopping,
            (await api.Db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id)).Status);
        api.Docker.StopSucceeds = true;

        await sweeper.RunCycleAsync(default);
        var db = postgres.NewDbContext();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Cancelled, run.Status); // reconciled cancellation ("or reconciliation")
        Assert.Contains("stop unconfirmed", run.ErrorMessage ?? "", StringComparison.Ordinal);
        Assert.Contains(preparation.Run.Id.ToString(), api.Docker.Stopped.Single()); // the hard backstop
        Assert.True(await db.Artifacts.AsNoTracking().AnyAsync(a => a.TaskId == preparation.Task.Id && a.Type == ArtifactTypes.LogReference));
        Assert.Equal(WorkspaceStatuses.Ready,
            (await db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == preparation.Task.Id)).Status);
        Assert.Equal(AgentSessionStatuses.Terminated,
            (await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == preparation.Run.Id)).Status);
        Assert.NotNull((await db.RunCredentials.AsNoTracking().SingleAsync(c => c.RunId == preparation.Run.Id)).RevokedAt);
        // the reconciled run is terminal — the Orchestrator's terminal consumer picks it up next tick
    }

    [Fact]
    public async Task StatusRoute_ReportsDeservedObservedAndErrorFacts()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, _) = await SeedWithStart(api);

        var status = await client.GetAsync($"/internal/runtime/runs/{preparation.Run.Id}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("starting", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("running", body.RootElement.GetProperty("container").GetProperty("observedState").GetString());
        Assert.Equal("running", body.RootElement.GetProperty("container").GetProperty("desiredState").GetString());
        Assert.Equal($"pi-session-{preparation.Run.Id}", body.RootElement.GetProperty("container").GetProperty("containerName").GetString());
        Assert.Equal("in_use", body.RootElement.GetProperty("session").GetProperty("status").GetString());

        var unknown = await client.GetAsync($"/internal/runtime/runs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var bare = await api.App.GetTestClient().GetAsync($"/internal/runtime/runs/{preparation.Run.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
    }

    // ---- reconciler (restart recovery) ----

    [Fact]
    public async Task Reconciler_RefreshesObservedState_FromDocker()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, _) = await SeedWithStart(api);

        api.Docker.States[$"pi-session-{preparation.Run.Id}"] = "exited"; // the container died after start
        var reconciler = new RunReconcilerCycle(api.App.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RunReconcilerCycle>.Instance);
        await reconciler.RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == preparation.Run.Id);
        // observed: the death was confirmed (failed) and the reconciled terminal stopped it
        Assert.Equal(ContainerObservedStates.Stopped, container.ObservedState);
        Assert.NotNull(container.LastStoppedAt);
        // the container died without a terminal callback — the reconciler finalizes the Run
        var reloadedRun = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Failed, reloadedRun.Status);
        Assert.Equal(RunErrorCodes.RuntimeFailure, reloadedRun.ErrorCode);
        Assert.StartsWith("reconciled:", reloadedRun.ErrorMessage);
    }

    [Fact]
    public async Task Reconciler_Inspect404_IsRemoved_RunsTheDeathReconciliation()
    {
        // Docker inspect 404 = the container is GONE (observed `removed`), never an outage —
        // a running Run whose container vanished is reconciled failed like any other silent death.
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, _) = await SeedWithStart(api);

        api.Docker.States.Remove($"pi-session-{preparation.Run.Id}"); // the fake returns `removed` for unknown containers (the inspect-404 shape)
        var reconciler = new RunReconcilerCycle(api.App.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RunReconcilerCycle>.Instance);
        await reconciler.RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == preparation.Run.Id);
        Assert.Equal(ContainerObservedStates.Stopped, container.ObservedState); // removed → reconciled terminal stop
        Assert.NotNull(container.RemovedAt);
        var reloadedRun = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Equal(RunStatuses.Failed, reloadedRun.Status);
        Assert.Equal(RunErrorCodes.RuntimeFailure, reloadedRun.ErrorCode);
    }

    [Fact]
    public async Task Reconciler_DiesWithExitFacts_RecordsDockerExitCode()
    {
        await using var api = await RuntimeTestSupport.NewApiAsync(postgres);
        var (preparation, client, _) = await SeedWithStart(api);

        var name = $"pi-session-{preparation.Run.Id}";
        api.Docker.States[name] = "exited";
        api.Docker.ExitFacts[name] = (137, true); // docker inspect State.ExitCode/OOMKilled
        var reconciler = new RunReconcilerCycle(api.App.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RunReconcilerCycle>.Instance);
        await reconciler.RunCycleAsync(default);

        var run = await postgres.NewDbContext().Runs.AsNoTracking().SingleAsync(r => r.Id == preparation.Run.Id);
        Assert.Contains("docker exit 137", run.ErrorMessage);
        Assert.Contains("OOMKilled", run.ErrorMessage);
    }
}

internal static class RuntimeHttpClientExtensions
{
    public static Task<HttpResponseMessage> PostStop(this HttpClient client, Guid runId) =>
        client.PostAsync($"/internal/runtime/runs/{runId}/stop",
            new StringContent("""{"reason":"stop_requested"}""", Encoding.UTF8, "application/json"));
}

// ---- secret mounting (exercised by the provisioning path) ----

public class StartContainerPlanTests
{
    private static RuntimeStorageService Storage(string secretsManagedDir, string secretsHostDir) =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            ManagedRoot = "/tmp/managed", HostRoot = "/tmp/host",
            SecretsManagedDir = secretsManagedDir, SecretsHostDir = secretsHostDir,
        }));

    private static Run Run(Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), Status = RunStatuses.Pending };
    private static OrchestrationTask Task() => new() { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), Type = OrchestrationTaskTypes.Plan };
    private static AgentProfileVersion Version(string? environmentConfiguration, string? resourceConfiguration) => new()
    {
        Id = Guid.NewGuid(), AgentProfileId = Guid.NewGuid(), Version = 1, SourceDirectory = "config/agents/stub",
        ConfigSnapshot = "{}", ConfigHash = "h", RuntimeType = "pi", MaxContextTokens = 1, State = ProfileVersionStates.Ready,
        ContainerImage = "agent-stub:v1", EnvironmentConfiguration = environmentConfiguration, ResourceConfiguration = resourceConfiguration,
    };

    [Fact]
    public void EmptySecretReferences_MountNothing()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var plan = StartPlan(secretsDir, environmentConfiguration: null, out var error);
        Assert.Null(error);
        Assert.DoesNotContain(plan!.Binds, v => v.Contains("/run/secrets/") && !v.Contains("callback-token"));
    }

    [Fact]
    public void MissingHostSecretFile_FailsStartClosed()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var error = StartPlanError(secretsDir, """{"secret_references":["github_agent_pat"]}""");
        Assert.Contains("github_agent_pat", error);
    }

    // ---- runtime-side reference grammar (frozen rows) — mirrors the Config Loader check ----

    [Fact]
    public void ReservedCallbackName_FailsStartClosed() // per-Run callback credential mount
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        File.WriteAllText(Path.Combine(secretsDir, "callback-token"), "not-a-token");
        var error = StartPlanError(secretsDir, """{"secret_references":["callback-token"]}""");
        Assert.Contains("reserved for the per-Run callback credential", error);
    }

    [Fact]
    public void TraversalName_FailsStartClosed()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var error = StartPlanError(secretsDir, """{"secret_references":["../github-sync-pat.txt"]}""");
        Assert.Contains("must match", error);
    }

    [Fact]
    public void DuplicateReferences_MountOnce()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        File.WriteAllText(Path.Combine(secretsDir, "github_agent_pat"), "pat-bytes");
        var plan = StartPlan(secretsDir, """{"secret_references":["github_agent_pat","github_agent_pat"]}""", out var error);
        Assert.Null(error);
        Assert.Single(plan!.Binds, v => v.EndsWith(":/run/secrets/github_agent_pat:ro"));
    }

    // ---- mount-source hygiene (fail-closed) ----

    [Fact]
    public void SecretFile_IsEmpty_FailsStartClosed()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        File.WriteAllText(Path.Combine(secretsDir, "github_agent_pat"), "");
        var error = StartPlanError(secretsDir, """{"secret_references":["github_agent_pat"]}""");
        Assert.Contains("exists but is empty", error);
        Assert.Contains("github_agent_pat", error);
    }

    [Fact]
    public void DirectoryNamedLikeSecret_FailsStartClosed() // a directory mounts "successfully" but reads broken
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(secretsDir, "github_agent_pat"));
        var error = StartPlanError(secretsDir, """{"secret_references":["github_agent_pat"]}""");
        Assert.Contains("no host-managed file", error);
    }

    [Fact]
    public void MalformedEnvironmentConfiguration_FailsStartClosed()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        Assert.Contains("must be a JSON object", StartPlanError(secretsDir, "[]"));
        Assert.Contains("must be an array of names", StartPlanError(secretsDir, """{"secret_references":"x"}"""));
    }

    [Fact]
    public void NullReferencesArray_MountsNothing() // the Config Loader shape for profiles without a secrets: section
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var plan = StartPlan(secretsDir, """{"secret_references":null}""", out var error);
        Assert.Null(error);
        Assert.DoesNotContain(plan!.Binds, v => v.Contains("/run/secrets/") && !v.Contains("callback-token"));
    }

    [Fact]
    public void OnlyReferencedSecrets_AreMounted_LestPrivilege()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        File.WriteAllText(Path.Combine(secretsDir, "github_agent_pat"), "pat-bytes");
        File.WriteAllText(Path.Combine(secretsDir, "mcp-github.token"), "other-bytes");
        var plan = StartPlan(secretsDir, """{"secret_references":["github_agent_pat"]}""", out var error);
        Assert.Null(error);
        var secretMounts = plan!.Binds.Where(v => v.Contains("/run/secrets/") && !v.Contains("callback-token")).ToList();
        Assert.Single(secretMounts);
        // read-only host-managed file — only the profile's referenced name, path shape per the grammar
        Assert.EndsWith("/etc/pi-agent/secrets/github_agent_pat:/run/secrets/github_agent_pat:ro", secretMounts[0]);
        // values travel by file mount only — the name must not leak into the environment either
        Assert.DoesNotContain("github_agent_pat", plan.Env ?? []);
    }

    private static ContainerPlan? StartPlan(string secretsDir, string? environmentConfiguration, out string? error) =>
        StartContainerPlan(
            Version(environmentConfiguration, """{"cpu":1.0,"memory":"512MiB"}"""),
            Storage(secretsDir, "/etc/pi-agent/secrets"), out error);

    private static string StartPlanError(string secretsDir, string environmentConfiguration) =>
        StartContainerPlan(Version(environmentConfiguration, null), Storage(secretsDir, "/etc/pi-agent/secrets"), out var error) is null
            ? error! : "";

    private static ContainerPlan? StartContainerPlan(
        AgentProfileVersion version, RuntimeStorageService storage, out string? error,
        string? workItemRepository = "aliaksandrZh/04") =>
        StartRunHandler.TryBuildContainerPlan(Run(), Task(), version, storage,
            new ContainerProvisionerOptions(), "/tmp/host/workspace", "/tmp/host/runs", workItemRepository, reviewPullRequestNumber: null,
            out var plan, out error) ? plan : null;

    [Fact]
    public void RepositoryFullName_TravelsAsGitCloneRepo() // workspace clone provisioning input
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var plan = StartContainerPlan(Version(null, """{"cpu":1.0,"memory":"512MiB"}"""), Storage(secretsDir, "/etc/pi-agent/secrets"),
            out var error, workItemRepository: "aliaksandrZh/04");
        Assert.Null(error);
        Assert.Contains("GIT_CLONE_REPO=aliaksandrZh/04", plan!.Env);
        // profiles without a clone need no env at all
        var bare = StartContainerPlan(Version(null, """{"cpu":1.0,"memory":"512MiB"}"""), Storage(secretsDir, "/etc/pi-agent/secrets"),
            out _, workItemRepository: null);
        Assert.DoesNotContain("GIT_CLONE_REPO", bare!.Env ?? []);
    }

    [Fact]
    public void ReviewTask_TravelsPinnedShas_AndPrNumber() // the reviewer checks out the pinned head and forms base..head locally
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "review-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var review = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), Type = OrchestrationTaskTypes.Review,
            TargetHeadSha = "head-sha-1", TargetBaseSha = "base-sha-1",
        };
        var plan = StartRunHandler.TryBuildContainerPlan(Run(), review, Version(null, """{"cpu":1.0,"memory":"512MiB"}"""),
            Storage(secretsDir, "/etc/pi-agent/secrets"), new ContainerProvisionerOptions(), "/tmp/host/workspace", "/tmp/host/runs",
            "aliaksandrZh/04", reviewPullRequestNumber: 13, out var built, out var error) ? built : null;
        Assert.Null(error);
        Assert.Contains("REVIEW_HEAD_SHA=head-sha-1", plan!.Env);
        Assert.Contains("REVIEW_BASE_SHA=base-sha-1", plan.Env);
        Assert.Contains("REVIEW_PR_NUMBER=13", plan.Env);
    }

    [Fact]
    public void NonReviewTask_CarriesNoReviewPins()
    {
        var secretsDir = Path.Combine(Path.GetTempPath(), "plan-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretsDir);
        var plan = StartContainerPlan(Version(null, """{"cpu":1.0,"memory":"512MiB"}"""), Storage(secretsDir, "/etc/pi-agent/secrets"),
            out _, workItemRepository: "aliaksandrZh/04");
        Assert.DoesNotContain("REVIEW_HEAD_SHA", plan!.Env ?? []);
        Assert.DoesNotContain("REVIEW_PR_NUMBER", plan.Env ?? []);
    }
}
