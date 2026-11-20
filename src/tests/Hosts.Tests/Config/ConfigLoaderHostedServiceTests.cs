using System.Net;
using PiAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Config;
using PiAgent.Platform.Http;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// ConfigLoaderHostedService end-to-end over real postgres: parse → validate → identity check →
/// repo lookups → one-transaction sync → cold-start gate. GitHub is stubbed at the HTTP level.
/// Includes the pure ConfigurationState / config-sync readiness semantics (01 §13).
/// </summary>
public class ConfigLoaderHostedServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Pat = "s3cret-pat-value";

    private static (ServiceProvider Provider, ConfigurationState State) BuildProvider(
        string connectionString,
        ConfigLoaderOptions options,
        Func<string, GitHubClient>? github = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        services.AddSingleton(new ConfigurationState());
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<Func<string, GitHubClient>>(_
            => github ?? (_ => new GitHubClient(new HttpClient(new StubGithubHandler()) { BaseAddress = new Uri("https://api.github.com/") }, Pat)));
        services.AddTransient<ConfigSyncService>();
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<ConfigurationState>());
    }

    private static ConfigLoaderOptions OptionsFor(string configRoot, string? patFile = "/tmp/pat.txt", string? sourceCommitSha = null) =>
        new() { ConfigRoot = configRoot, PatFile = patFile, SourceCommitSha = sourceCommitSha };

    private static async Task<(ConfigLoaderHostedService Service, ConfigurationState State)> BuildService(
        PostgresFixture postgres,
        string configRoot,
        string? patFile = "/tmp/pat.txt",
        Func<string, GitHubClient>? github = null,
        string? sourceCommitSha = null,
        Action<int>? processExit = null)
    {
        if (patFile is null || patFile == "/tmp/pat.txt")
            File.WriteAllText(patFile ?? "/tmp/pat.txt", Pat);
        var options = OptionsFor(configRoot, patFile, sourceCommitSha);
        var (provider, state) = BuildProvider(postgres.ConnectionString, options, github);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return (new ConfigLoaderHostedService(
            scopeFactory, state, Microsoft.Extensions.Options.Options.Create(options),
            provider.GetRequiredService<Func<string,GitHubClient>>(),
            NullLogger<ConfigLoaderHostedService>.Instance, processExit ?? (_ => { })), state);
    }

    [Fact]
    public async Task StartAsync_ValidConfig_Synchronizes_MarksReady_AndPersistRows()
    {
        await postgres.ResetAsync();
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root,
            repositories: ConfigSamples.RepositoriesOneEntry,
            routing: ConfigSamples.RoutingPlanOnly);
        var (service, state) = await BuildService(postgres, root, sourceCommitSha: "abc123");

        await service.StartAsync(CancellationToken.None);

        Assert.True(state.Synchronized);
        var check = new ConfigSyncReadinessCheck(state);
        Assert.True((await check.CheckAsync(CancellationToken.None)).Ok);
        await using var db = postgres.NewDbContext();
        Assert.Equal(1, await db.Repositories.CountAsync());
        Assert.Equal(1, await db.AgentProfileVersions.CountAsync());
        var version = await db.AgentProfileVersions.SingleAsync();
        Assert.Equal("abc123", version.SourceCommitSha);
    }

    [Fact]
    public async Task StartAsync_MismatchedBotLogin_Throws_NamesBothLogins_LeavesDbEmpty()
    {
        await postgres.ResetAsync();
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root);
        File.WriteAllText("/tmp/pat.txt", Pat);
        var (service, state) = await BuildService(postgres, root, github: _ => new GitHubClient(
            new HttpClient(new StubGithubHandler(userLogin: "someone-else")) { BaseAddress = new Uri("https://api.github.com/") }, Pat));

        var exception = await Assert.ThrowsAsync<ConfigException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("'pi-bot'", exception.Message);
        Assert.Contains("'someone-else'", exception.Message);
        Assert.DoesNotContain(Pat, exception.Message);
        Assert.False(state.Synchronized);
        await using var db = postgres.NewDbContext();
        Assert.Equal(0, await db.Repositories.CountAsync());
    }

    [Fact]
    public async Task StartAsync_MissingPatFile_ThrowsNamingPath()
    {
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root);
        var (service, state) = await BuildService(postgres, root, patFile: "/nonexistent/github-sync-pat.txt");

        var exception = await Assert.ThrowsAsync<ConfigException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("github-sync-pat.txt", exception.Message);
        Assert.False(state.Synchronized);
    }

    [Fact]
    public async Task StartAsync_UnknownRepository_ThrowsNamingRepo()
    {
        await postgres.ResetAsync();
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root,
            repositories: ConfigSamples.RepositoriesOneEntry,
            routing: ConfigSamples.RoutingPlanOnly);
        File.WriteAllText("/tmp/pat.txt", Pat);
        var (service, state) = await BuildService(postgres, root, github: _ => new GitHubClient(
            new HttpClient(new StubGithubHandler(repoStatus: HttpStatusCode.NotFound)) { BaseAddress = new Uri("https://api.github.com/") }, Pat));

        var exception = await Assert.ThrowsAsync<ConfigException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("example-org/example-repo", exception.Message);
        Assert.Contains("404", exception.Message);
        Assert.False(state.Synchronized);
    }

    [Fact]
    public async Task StartAsync_SchemaInvalid_Throws_AndNoDbRows()
    {
        await postgres.ResetAsync();
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root, system: ConfigSamples.System + "\nmystery_knob: 5\n");
        File.WriteAllText("/tmp/pat.txt", Pat);
        var (service, state) = await BuildService(postgres, root);

        await Assert.ThrowsAsync<ConfigException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(state.Synchronized);
        await using var db = postgres.NewDbContext();
        Assert.Equal(0, await db.Repositories.CountAsync());
    }

    [Fact]
    public async Task StartAsync_DbDown_Throws_AndStateFailed()
    {
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root);
        File.WriteAllText("/tmp/pat.txt", Pat);
        var (provider, state) = BuildProvider(
            "Host=localhost;Port=1;Database=x;Username=x;Password=x", OptionsFor(root));
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var service = new ConfigLoaderHostedService(
            scopeFactory, state, Microsoft.Extensions.Options.Options.Create(OptionsFor(root)),
            _ => new GitHubClient(new HttpClient(new StubGithubHandler()) { BaseAddress = new Uri("https://api.github.com/") }, Pat),
            NullLogger<ConfigLoaderHostedService>.Instance,
            _ => { });

        await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(CancellationToken.None));
        Assert.False(state.Synchronized);
        Assert.NotNull(state.Error);
    }

    // --- deterministic failure: the process must ask to exit even though StartAsync rethrows ---
    // (owner incident 2026-11-20: StartAsync throw left the container alive spinning at 100% CPU,
    // so compose's restart policy never fired)

    [Fact]
    public async Task StartAsync_Failure_TriggersProcessExitHook_WithCode1()
    {
        await postgres.ResetAsync();
        var root = Directory.CreateTempSubdirectory("loader").FullName;
        ConfigSamples.WriteSampleTree(root, system: ConfigSamples.System.Replace("poll_interval: 60s", "poll_interval: 10s"));
        var exitCodes = new List<int>();
        var (service, state) = await BuildService(postgres, root, processExit: exitCodes.Add);

        await Assert.ThrowsAsync<ConfigException>(() => service.StartAsync(CancellationToken.None));

        Assert.Equal([1], exitCodes);
        Assert.False(state.Synchronized);
    }

    // --- cold-start gate primitives (01 §13) ---

    [Fact]
    public async Task ConfigurationState_GatesReadiness()
    {
        var state = new ConfigurationState();
        var check = new ConfigSyncReadinessCheck(state);
        var before = await check.CheckAsync(CancellationToken.None);
        Assert.False(before.Ok);
        Assert.Equal("config-sync", check.Name);

        state.MarkSynchronized("test-bot", 10);
        Assert.True((await check.CheckAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ConfigurationState_FailureSurfacesInReadiness()
    {
        var state = new ConfigurationState();
        state.MarkFailed("system.yaml: poll_interval (10s) must be ≥ 30s");
        var result = await new ConfigSyncReadinessCheck(state).CheckAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("poll_interval", result.Error);
    }
}

/// <summary>Stub GitHub REST for loader tests: /user and /repos/*, per-instance canned statuses.</summary>
file sealed class StubGithubHandler(
    string userLogin = "pi-bot",
    HttpStatusCode repoStatus = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        if (path == "/user")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"login\": \"{userLogin}\"}}"),
            });
        }

        if (path.StartsWith("/repos/") && repoStatus == HttpStatusCode.OK)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id": 4242, "full_name": "example-org/example-repo", "default_branch": "main"}"""),
            });
        }

        return Task.FromResult(new HttpResponseMessage(repoStatus));
    }
}
