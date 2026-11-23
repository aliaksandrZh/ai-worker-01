using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.RuntimeApi;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>Decision 82: the Docker provisioner's failure semantics — the rules Komodo's locks used to
/// provide. 404 = the container is gone (idempotent success); daemon/socket failures throw (desired
/// state preserved, callers retry); a create-name collision force-removes the stale container and retries.</summary>
public class DockerProvisioningTests
{
    /// <summary>Scripted transport: the test decides what Docker answers per request.</summary>
    private sealed class StubDockerHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
        }
    }

    private static DockerProvisioningClient Client(StubDockerHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<DockerProvisioningClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ContainerPlan Plan(string name = "pi-session-x") =>
        new(Name: name, Image: "agent-stub:v1", Env: ["A=1"], Binds: ["/h:/c:ro"],
            Labels: new Dictionary<string, string> { ["pi-run"] = "r" }, Cpus: null, MemoryBytes: null, Network: "pi-agent-net");

    private static string CreateBody => """{"Id":"abc123","Warnings":[]}""";

    private static string InspectBody(string status, long exitCode = 0, bool oom = false) =>
        "{" + $"\"State\":{{\"Status\":\"{status}\",\"ExitCode\":{exitCode},\"OOMKilled\":{(oom ? "true" : "false")}}}" + "}";

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotModified, true)] // already stopped
    [InlineData(HttpStatusCode.NotFound, true)] // already gone — idempotent, never retried forever
    public async Task Stop_ToleratesAlreadyGone(HttpStatusCode status, bool expected)
    {
        var handler = new StubDockerHandler { Responder = _ => new HttpResponseMessage(status) };
        Assert.Equal(expected, await Client(handler).StopAsync("pi-session-x", default));
    }

    [Fact]
    public async Task Stop_PausedOrRestarting_CannotStopNow_False()
    {
        var handler = new StubDockerHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.Conflict) };
        Assert.False(await Client(handler).StopAsync("pi-session-x", default));
    }

    [Fact]
    public async Task Stop_DaemonFailure_Throws_DesiredStatePreserved()
    {
        var handler = new StubDockerHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") } };
        var provisioner = Client(handler);
        await Assert.ThrowsAsync<DockerProvisioningException>(() => provisioner.StopAsync("pi-session-x", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotFound, true)] // already removed
    public async Task Remove_ToleratesAlreadyGone(HttpStatusCode status, bool expected)
    {
        var handler = new StubDockerHandler { Responder = _ => new HttpResponseMessage(status) };
        Assert.Equal(expected, await Client(handler).RemoveAsync("pi-session-x", default));
    }

    [Fact]
    public async Task Inspect_404_ReportsRemoved_NeverUnknown()
    {
        var handler = new StubDockerHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var snapshot = await Client(handler).GetContainerStateAsync("pi-session-x", default);
        Assert.Equal("removed", snapshot.State); // observable machine sees the removal, the outage path throws instead
    }

    [Fact]
    public async Task Inspect_DaemonFailure_Throws_DesiredStatePreserved()
    {
        var handler = new StubDockerHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var provisioner = Client(handler);
        await Assert.ThrowsAsync<DockerProvisioningException>(() => provisioner.GetContainerStateAsync("pi-session-x", default));
    }

    [Fact]
    public async Task Inspect_MapsExitFacts()
    {
        var handler = new StubDockerHandler { Responder = _ => Json(HttpStatusCode.OK, InspectBody("exited", exitCode: 137, oom: true)) };
        var snapshot = await Client(handler).GetContainerStateAsync("pi-session-x", default);
        Assert.Equal("exited", snapshot.State);
        Assert.Equal(137, snapshot.ExitCode);
        Assert.True(snapshot.OomKilled);
    }

    [Fact]
    public async Task Create_NameCollision_ForceRemovesTheStaleContainer_AndRetries()
    {
        var handler = new StubDockerHandler
        {
            Responder = request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                return path.Contains("containers/create")
                    ? Json(HttpStatusCode.Conflict, """{"message":"Conflict. The container name is already in use"}""")
                    : path.Contains("force=true")
                        ? new HttpResponseMessage(HttpStatusCode.NoContent)
                        : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            },
        };
        var created = await Client(handler).CreateContainerAsync(Plan(), default);

        Assert.Null(created); // the retry collides again in this stub — no infinite loop, caller sees the failure
        var delete = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        Assert.Contains("force=true", delete.RequestUri!.Query); // the stale container is force-removed even if running
    }

    [Fact]
    public async Task Create_NameCollision_ThenFreshCreate_Succeeds()
    {
        var createCalls = 0;
        var handler = new StubDockerHandler
        {
            Responder = request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path.Contains("containers/create"))
                    return ++createCalls == 1
                        ? Json(HttpStatusCode.Conflict, """{"message":"Conflict"}""")
                        : Json(HttpStatusCode.Created, CreateBody);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
        };
        var created = await Client(handler).CreateContainerAsync(Plan(), default);

        Assert.Equal("abc123", created);
        Assert.Equal(2, createCalls);
        Assert.Contains("force=true", handler.Requests.Single(r => r.Method == HttpMethod.Delete).RequestUri!.Query);
    }

    [Fact]
    public async Task Create_Payload_CarriesImageEnvBindsLimitsNetwork()
    {
        string? payload = null;
        var handler = new StubDockerHandler
        {
            Responder = request =>
            {
                if (request.RequestUri!.PathAndQuery.Contains("containers/create"))
                    payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.Created, CreateBody);
            },
        };
        var plan = Plan() with { Cpus = 1.5m, MemoryBytes = 2L * 1024 * 1024 * 1024 };
        await Client(handler).CreateContainerAsync(plan, default);

        using var doc = System.Text.Json.JsonDocument.Parse(payload!);
        var root = doc.RootElement;
        Assert.Equal("agent-stub:v1", root.GetProperty("Image").GetString());
        Assert.Equal("A=1", root.GetProperty("Env").EnumerateArray().Single().GetString());
        Assert.Equal("r", root.GetProperty("Labels").GetProperty("pi-run").GetString());
        Assert.True(root.GetProperty("HostConfig").GetProperty("Init").GetBoolean());
        Assert.Equal(1_500_000_000L, root.GetProperty("HostConfig").GetProperty("NanoCpus").GetInt64());
        Assert.Equal(2L * 1024 * 1024 * 1024, root.GetProperty("HostConfig").GetProperty("Memory").GetInt64());
        Assert.Equal("pi-agent-net", root.GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
    }
}
