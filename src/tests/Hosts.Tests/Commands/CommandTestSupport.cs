using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Poller;

namespace PiAgent.Hosts.Tests.Commands;

public sealed record ProcessorHarness(
    ServiceProvider Services,
    CommentCommandProcessor Processor,
    PollerTestSupport.StubHandler Handler,
    ConfigurationState ConfigState,
    CommandMetrics Metrics);

/// <summary>
/// Seeds for the comment-command processor tests: repository (with the shape the Config Loader syncs),
/// work item, conversation comment, and the `issue_comment_created` event driving processing.
/// </summary>
public static class CommandTestSupport
{
    private static long _nextId = 4200;
    private static int _nextNumber = 100;

    private static long NextCommentId() => Interlocked.Increment(ref _nextId);

    /// <summary>Full seeded configuration (write baseline: emptied allowlist, all commands, both types).</summary>
    public const string DefaultConfiguration = """
        {"allowlist": {"enabled": false, "logins": []}, "permitted_commands": ["plan", "code", "review", "test_cases", "status", "stop", "help", "retry"], "permitted_work_item_types": ["issue", "pull_request"], "authorization": {"minimum_permission": "write"}}
        """;

    public static async Task<(Repository Repo, WorkItem WorkItem, GitHubComment Comment)> SeedAsync(
        AppDbContext db,
        bool pullRequest = false,
        string? configuration = null,
        string? authorLogin = "alice",
        bool enabled = true,
        string? body = null,
        bool editedAlready = false,
        string? author = null,
        Repository? existingRepository = null)
    {
        var repository = existingRepository ?? new Repository
        {
            Id = Guid.NewGuid(),
            GithubRepositoryId = 900001,
            Owner = "octo-org",
            Name = "example",
            FullName = "octo-org/example",
            DefaultBranch = "main",
            IsEnabled = enabled,
            Configuration = configuration ?? DefaultConfiguration,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        if (existingRepository is null)
            db.Repositories.Add(repository);
        var repositoryId = repository.Id;

        var number = _nextNumber++;
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            GithubNodeId = $"node-{number}",
            Number = number,
            Type = pullRequest ? WorkItemTypes.PullRequest : WorkItemTypes.Issue,
            Title = pullRequest ? "A pull request" : "An issue",
            Body = "work item body",
            State = "open",
            AuthorLogin = author ?? "someone",
            Assignees = "[]",
            Labels = "[]",
            CreatedAtGithub = DateTime.UtcNow.AddDays(-1),
            UpdatedAtGithub = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow,
        };
        db.WorkItems.Add(workItem);

        var createdAt = DateTime.UtcNow.AddMinutes(-5);
        var updated = editedAlready ? createdAt.AddMinutes(1) : createdAt;
        var comment = new GitHubComment
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = NextCommentId(),
            Kind = GitHubCommentKinds.IssueComment,
            AuthorLogin = authorLogin,
            Body = body ?? "@jsxhuz /plan",
            CreatedAtGithub = createdAt,
            UpdatedAtGithub = updated,
            LastSyncedAt = DateTime.UtcNow,
        };
        db.GitHubComments.Add(comment);
        await db.SaveChangesAsync();
        return (repository, workItem, comment);
    }

    public static Task<GitHubEvent> SeedCommandEventAsync(AppDbContext db, GitHubComment comment, WorkItem? workItem = null)
    {
        var detected = workItem ?? db.WorkItems.Local.First(w => w.Id == comment.WorkItemId);
        var created = new GitHubEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = detected.RepositoryId,
            WorkItemId = detected.Id,
            Source = GitHubEventSources.RestResourceSync,
            ExternalEventId = null,
            DedupeKey = $"ic:{comment.GithubCommentId}:created",
            EventType = GitHubEventTypes.IssueCommentCreated,
            Payload = JsonSerializer.Serialize(new
            {
                comment_id = comment.GithubCommentId,
                issue_number = detected.Number,
                author = comment.AuthorLogin,
            }, EventWriter.PayloadOptions),
            OccurredAtGithub = comment.CreatedAtGithub,
            ReceivedAt = DateTime.UtcNow,
            ProcessingStatus = GitHubEventStatuses.Pending,
        };
        db.GitHubEvents.Add(created);
        return db.SaveChangesAsync().ContinueWith(_ => created);
    }

    /// <summary>A processor wired against the real postgres fixture with a stubbed GitHub handler.</summary>
    public static ProcessorHarness NewProcessor(string connectionString, ConfigurationState? configurationState = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        var configuration = configurationState ?? new ConfigurationState();
        configuration.MarkSynchronized("jsxhuz");

        var handler = new PollerTestSupport.StubHandler();
        var gate = new CollaboratorPermissionGate(
            new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, "test-pat");

        var metrics = new CommandMetrics();
        services.AddSingleton(configuration);
        services.AddSingleton(gate);
        services.AddSingleton(metrics);
        services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Debug));

        var provider = services.BuildServiceProvider();
        var processor = new CommentCommandProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            gate,
            metrics,
            provider.GetRequiredService<ILogger<CommentCommandProcessor>>());
        return new ProcessorHarness(provider, processor, handler, configuration, metrics);
    }
}
