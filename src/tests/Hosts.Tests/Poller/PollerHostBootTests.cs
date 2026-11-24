using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiAgent.Poller;
using PiAgent.Platform.Workers;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Boot smoke test over the real Poller Program.cs DI graph (WebApplicationFactory, same
/// pattern as RuntimeApiEndpointsTests with DockerProvisioningClient). The host's outside
/// dependencies are faked cheaply: PI_AGENT_CONFIG_DIR points at a temp config dir (system.yaml
/// + projects.yaml are startup-required), the PAT at a temp file, the DB at a fake connection
/// string (AddPiData does not connect at startup; the WorkerLoop tolerates cycle failures, so
/// the fake DB only shows up as a /readyz failure).
/// The regression this guards: ProjectItemsCycle wants IOptions&lt;ProjectSyncOptions&gt; — a bare
/// AddSingleton(ProjectSyncConfig.Read(...)) leaves that wrapper unwired, the options resolve
/// system-empty and the projects pass silently skips (no exception, no log). Options.Create is
/// required. Asserts both IWorkerCycle units resolve AND carry the values read from the config
/// files, plus the /readyz failure naming the unreachable postgres.
/// </summary>
public sealed class PollerHostBootTests : IDisposable
{
    private readonly string _root;
    private readonly WebApplicationFactory<PollerOptions> _factory;

    public PollerHostBootTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"poller-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "system.yaml"),
            "poll_interval: 90s\npoll_overlap: 2m\nper_page: 50\n");
        File.WriteAllText(Path.Combine(_root, "projects.yaml"),
            "projects:\n  - owner: aliaksandrzh\n    number: 7\n");
        File.WriteAllText(Path.Combine(_root, "pat.txt"), "test-pat-never-real");

        _factory = new WebApplicationFactory<PollerOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultDb", "Host=127.0.0.1;Database=test;Username=test;Password=test");
            builder.UseSetting("PI_AGENT_CONFIG_DIR", _root);
            builder.UseSetting("PI_AGENT_GITHUB_PAT_FILE", Path.Combine(_root, "pat.txt"));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp best-effort */ }
    }

    [Fact]
    public void ProjectItemsCycle_IsRegistered_AndProjectSyncOptionsCarryProjectsYaml()
    {
        var cycles = _factory.Services.GetServices<IWorkerCycle>().ToList();
        Assert.Contains(cycles, cycle => cycle is ProjectItemsCycle);

        // The exact wiring that regressed: without Options.Create in Program.cs this resolves
        // an empty wrapper and the projects pass silently no-ops.
        var options = _factory.Services.GetRequiredService<IOptions<ProjectSyncOptions>>().Value;
        var project = Assert.Single(options.Projects);
        Assert.Equal("aliaksandrzh", project.Owner);
        Assert.Equal(7, project.Number);
    }

    [Fact]
    public void PollerOptions_CarrySystemYaml()
    {
        var options = _factory.Services.GetRequiredService<IOptions<PollerOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(90), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Overlap);
        Assert.Equal(50, options.PerPage);
    }

    [Fact]
    public async Task Readyz_NamesPostgres_WhenDbUnreachable()
    {
        var response = await _factory.CreateClient().GetAsync("/readyz");

        Assert.Equal(503, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("failed").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "postgres");
    }
}
