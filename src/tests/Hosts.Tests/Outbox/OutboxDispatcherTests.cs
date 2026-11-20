using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.OutboxWorker;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// OutboxDispatcher decision-67 behavior over real postgres with a stubbed GitHub (task 03 plan
/// Review Focus items 1/2/5): classification of permanent vs transient and rate-limit 403, marker
/// dedup before every re-send, lease/window exhaustion, payload guarding, token never logged,
/// OutboxState refresh.
/// </summary>
public class OutboxDispatcherTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Pat = "s3cret-write-pat";
    private const int MaxIntervalHours = 1;
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    private sealed class StubGithubHandler : HttpMessageHandler
    {
        public int PostCount;
        public int GetCount;
        public readonly List<string> Bodies = [];
        public List<string> LatestBodies = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"message": "boom"}""", Encoding.UTF8, "application/json"),
            };

        /// <summary>Defaults to a healthy empty listing; set to force a GitHub failure on the marker-check GET.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? GetRespond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref GetCount);
                if (GetRespond is { } getRespond)
                    return getRespond(request);
                var array = JsonSerializer.Serialize(
                    LatestBodies.Select(b => new { body = b }),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(array, Encoding.UTF8, "application/json"),
                };
            }

            Interlocked.Increment(ref PostCount);
            if (request.Content is { } content)
                Bodies.Add(await content.ReadAsStringAsync(cancellationToken));
            return Respond(request);
        }
    }

    private sealed class CapturingLogger : ILogger<OutboxDispatcher>
    {
        public readonly List<string> Lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static HttpResponseMessage Status(HttpStatusCode statusCode, string body = """{"message": "nope"}""", int? remaining = null, int? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (remaining is { } r) response.Headers.Add("X-RateLimit-Remaining", r.ToString());
        if (retryAfter is { } ra) response.Headers.Add("Retry-After", ra.ToString());
        return response;
    }

    private (OutboxDispatcher Dispatcher, StubGithubHandler Handler, OutboxState State, CapturingLogger? Logs) NewDispatcher(
        CapturingLogger? logger = null)
    {
        var handler = new StubGithubHandler();
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<OutboxActionStore>();
        var provider = services.BuildServiceProvider();

        var state = new OutboxState();
        var client = new GitHubWriteClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, Pat);
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            client,
            Options.Create(new OutboxWorkerOptions(TimeSpan.FromHours(24))),
            state,
            (ILogger<OutboxDispatcher>?)logger ?? NullLogger<OutboxDispatcher>.Instance);
        return (dispatcher, handler, state, logger);
    }

    private static string MarkerOf(string key) => $"<!-- pi-agent:outbox:{key} -->";

    private async Task<Guid> SeedDueAsync(string? key = null, string? payload = null, DateTime? createdAt = null, int attempts = 0)
    {
        var repositoryId = await OutboxTestSeed.RepositoryAsync(postgres);
        return await OutboxTestSeed.ActionAsync(postgres, repositoryId, key: key, payload: payload, createdAt: createdAt, attempts: attempts);
    }

    private async Task MakeDueAsync(Guid actionId)
    {
        await using var db = postgres.NewDbContext();
        await db.OutboxActions.Where(a => a.Id == actionId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(a => a.AvailableAt, DateTime.UtcNow));
    }

    [Fact]
    public async Task Success_CompletesWithMarker_VisibleTextUntouched()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(key: "k-success", payload: OutboxTestSeed.PayloadFor(42, "Final failure: /code"));
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.Created, """{"id": 9001}""");

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        var sent = JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("body").GetString()!;
        Assert.StartsWith("<!-- pi-agent:outbox:k-success -->\n\n", sent);
        Assert.Equal("Final failure: /code", sent["<!-- pi-agent:outbox:k-success -->\n\n".Length..]);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.Completed, row.Status);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task RateLimit403_TransientWithRetryAfter()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync();
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.Forbidden, remaining: 0, retryAfter: 60);

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotEqual(OutboxStatuses.DeadLetter, row.Status);
        Assert.True(row.AvailableAt >= DateTime.UtcNow.AddSeconds(60) - Tolerance
            && row.AvailableAt <= DateTime.UtcNow.AddSeconds(60) + Tolerance, $"available_at {row.AvailableAt:O}");
    }

    [Fact]
    public async Task Regular403_DeadLetter()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync();
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.Forbidden, body: """{"message": "write access denied"}""");

        await dispatcher.RunCycleAsync(CancellationToken.None);
        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Contains("HTTP 403", row.LastError);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task PermanentStatuses_DeadLetterImmediately(int statusCode)
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync();
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status((HttpStatusCode)statusCode);

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Contains($"HTTP {statusCode}", row.LastError);
        Assert.NotNull(row.Payload);
    }

    [Fact]
    public async Task ServerError_BackoffDoubles()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync();
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.InternalServerError);

        await dispatcher.RunCycleAsync(CancellationToken.None);
        var afterFirst = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        var firstDelay = afterFirst.AvailableAt - DateTime.UtcNow;
        await MakeDueAsync(actionId);
        await dispatcher.RunCycleAsync(CancellationToken.None);
        var afterSecond = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        var secondDelay = afterSecond.AvailableAt - DateTime.UtcNow;

        Assert.Equal(OutboxStatuses.Pending, afterSecond.Status);
        Assert.Equal(2, afterSecond.Attempts);
        Assert.True(Math.Abs((firstDelay - TimeSpan.FromSeconds(8)).TotalSeconds) <= Tolerance.TotalSeconds, $"first delay {firstDelay}");
        Assert.True(Math.Abs((secondDelay - TimeSpan.FromSeconds(16)).TotalSeconds) <= Tolerance.TotalSeconds, $"second delay {secondDelay}");
    }

    [Fact]
    public async Task AmbiguousError_MarkerCheckSuppressesRepost()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(key: "k-amb");
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => throw new HttpRequestException("connection reset mid-body");

        await dispatcher.RunCycleAsync(CancellationToken.None);

        var afterFirst = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.Pending, afterFirst.Status);
        Assert.StartsWith("ambiguous:", afterFirst.LastError);

        await MakeDueAsync(actionId);
        handler.LatestBodies = [MarkerOf("k-amb") + "\n\nbody"];
        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.Completed, row.Status);
    }

    [Fact]
    public async Task ServerError_MarkerCheckSuppressesRepost()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(key: "k-500");
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.InternalServerError);

        await dispatcher.RunCycleAsync(CancellationToken.None);
        await MakeDueAsync(actionId);
        handler.LatestBodies = [MarkerOf("k-500") + "\n\nbody"];
        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.Completed, row.Status);
    }

    [Fact]
    public async Task RetryWindowExhausted_DeadLetter_NoPost()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(attempts: 1, createdAt: DateTime.UtcNow.AddHours(-25));
        var (dispatcher, handler, _, _) = NewDispatcher();

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Contains("retry window exhausted", row.LastError);
    }

    [Fact]
    public async Task InvalidPayload_DeadLetter()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(payload: "{}");
        var (dispatcher, handler, _, _) = NewDispatcher();

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, handler.PostCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Contains("payload is not a valid outbox comment payload", row.LastError);
    }

    [Fact]
    public async Task TokenNeverLogged()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync();
        var logger = new CapturingLogger();
        var (dispatcher, handler, _, logs) = NewDispatcher(logger);
        // hostile response: the 401 body echoes the PAT back — it must never reach the logs or last_error
        handler.Respond = _ => Status(HttpStatusCode.Unauthorized, body: $$"""{"message": "Bad credentials ({{Pat}})"}""");

        await dispatcher.RunCycleAsync(CancellationToken.None);

        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.DoesNotContain(Pat, row.LastError);
        Assert.Contains("Bad credentials", row.LastError);
        Assert.NotNull(logs);
        Assert.All(logs.Lines, line => Assert.DoesNotContain(Pat, line));
        Assert.Contains(logs.Lines, l => l.Contains("outbox dead_letter"));
        var warning = logs.Lines.Single(l => l.Contains("outbox dead_letter"));
        Assert.Contains("401", warning);
    }

    [Fact]
    public async Task FirstAttempt_DoesNotProbeGitHub()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(key: "k-first");
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.Created, """{"id": 1001}""" );

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, handler.PostCount);
        Assert.Equal(0, handler.GetCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.Completed, row.Status);
    }

    [Fact]
    public async Task GetFailure_ClassifiesLikeWrite()
    {
        await postgres.ResetAsync();
        var actionId = await SeedDueAsync(key: "k-retry", attempts: 1);
        var (dispatcher, handler, _, _) = NewDispatcher();
        handler.GetRespond = _ => Status(HttpStatusCode.Unauthorized);

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, handler.PostCount);
        Assert.Equal(1, handler.GetCount);
        var row = await OutboxTestSeed.ReloadAsync(postgres, actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Contains("HTTP 401", row.LastError);
        Assert.Equal(2, row.Attempts);
    }

    [Fact]
    public async Task MetricsUpdated_AfterDeadLetter()
    {
        await postgres.ResetAsync();
        await SeedDueAsync();
        var (dispatcher, handler, state, _) = NewDispatcher();
        handler.Respond = _ => Status(HttpStatusCode.Unauthorized);

        await dispatcher.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, state.Pending);
        Assert.Equal(0, state.Failed);
        Assert.Equal(1, state.DeadLetter);
    }
}
