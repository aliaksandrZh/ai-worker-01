using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// The Config Loader — a startup component of the Orchestrator: validates the
/// YAML configuration plane, checks `github.bot_login` against the PAT identity (`GET /user`), looks up
/// configured repositories (`GET /repos/{owner}/{repo}`), and persists Repository rows + sync cursors,
/// AgentProfile/AgentProfileVersion rows (state `draft`) and the seeded routing rules in one transaction.
/// Schema-invalid YAML fails before any DB work; every failure is a diagnosable startup configuration
/// error. The loader never builds or dispatches image builds.
/// </summary>
public sealed class ConfigLoaderHostedService(
    IServiceScopeFactory scopeFactory,
    ConfigurationState state,
    IOptions<ConfigLoaderOptions> options,
    Func<string, GitHubClient> githubClientFactory,
    ILogger<ConfigLoaderHostedService> logger,
    Action<int> processExit) : IHostedService
{
    /// <summary>
    /// Owner incident 2026-11-20: throwing from StartAsync left the container alive spinning at 100%
    /// CPU (host abort kept the process alive), so compose's restart policy never fired. On failure
    /// the loader logs the diagnosable error, then asks for a hard process exit before rethrowing.
    /// </summary>
    private readonly Action<int> _processExit = processExit;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configRoot = options.Value.ConfigRoot;
            var parsed = ConfigParser.Parse(configRoot);

            var violations = ConfigValidator.Validate(parsed);
            if (violations.Count > 0)
                throw new ConfigException(
                    $"configuration validation failed with {violations.Count} violation(s):\n{string.Join("\n", violations)}");

            var pat = ReadPat(options.Value.PatFile);
            var client = githubClientFactory(pat);

            var identity = await client.GetAuthenticatedLoginAsync(cancellationToken);
            if (!string.Equals(identity, parsed.System.BotLogin, StringComparison.Ordinal))
                throw new ConfigException(
                    $"configuration error: github.bot_login '{parsed.System.BotLogin}' does not match the PAT identity '{identity}' (GET /user)");

            var repositoryInfos = new Dictionary<string, GitHubRepoInfo>();
            foreach (var entry in parsed.Repositories)
            {
                var fullName = $"{entry.Owner}/{entry.Name}";
                try
                {
                    repositoryInfos[fullName.ToLowerInvariant()] = await client.GetRepositoryAsync(entry.Owner, entry.Name, cancellationToken);
                }
                catch (ConfigException exception)
                {
                    throw new ConfigException($"configuration error for repository {fullName}: {exception.Message}");
                }
            }

            var snapshots = parsed.Profiles
                .Select(profile => ConfigSnapshotBuilder.Build(
                    profile,
                    Path.Combine(configRoot, "agents", profile.Name),
                    options.Value.SourceCommitSha,
                    options.Value.RuntimeBridgeSourceDir))
                .ToList();

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
            var result = await new ConfigSyncService(db).SyncAsync(
                new ConfigSynchronization(parsed, repositoryInfos, snapshots, options.Value.SourceCommitSha), cancellationToken);

            state.SetProjectBoardTriggers(parsed.ProjectBoardTriggers);
            state.SetProjectStatusConfigs(parsed.ProjectStatusConfigs);
            state.SetReviewLoopConfigs(parsed.ReviewLoopConfigs);
            state.MarkSynchronized(parsed.System.BotLogin, parsed.System.MaxConcurrentRuns);
            logger.LogInformation(
                "configuration synchronized from {root}: repositories created {created} (cursors {cursors}), {versions} new agent profile version(s), {rules} routing rule(s) upserted, {deleted} deleted",
                configRoot, result.RepositoriesCreated, result.CursorsCreated, result.VersionsAdded, result.RoutingRulesUpserted, result.RoutingRulesDeleted);
        }
        catch (Exception exception)
        {
            state.MarkFailed(exception.Message);
            logger.LogError(exception, "configuration synchronization failed — startup aborted (cold-start gate stays closed)");
            _processExit(1);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The single read-only GitHub PAT read once per host start: the Config Loader
    // validates the bot identity with it at startup and the comment-authorization gate performs the
    // collaborator permission checks with it (task 06). Rotation = host restart with a replaced
    // secret file; failure is a diagnosable startup configuration error, token content never
    // appears in messages. Reading is the shared Platform discipline (formerly a local
    // SyncPat copy with identical message shapes).
    private static string ReadPat(string? patFile) =>
        PiAgent.Platform.Secrets.PatFile.Read(patFile, "PI_AGENT_GITHUB_PAT_FILE",
            "the Config Loader must verify the bot identity against GET /user");
}
