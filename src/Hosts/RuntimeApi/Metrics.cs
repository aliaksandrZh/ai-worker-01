using Microsoft.Extensions.Logging;
using Npgsql;
using PiAgent.Platform.Workers;

namespace PiAgent.RuntimeApi;

/// <summary>Refreshes the §14 "database size" metric every worker tick.</summary>
public sealed class DbSizeCycle(NpgsqlDataSource dataSource, ILogger<DbSizeCycle> logger) : IWorkerCycle
{
    public long? DbSizeBytes { get; private set; }

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT pg_database_size(current_database())::bigint");
            DbSizeBytes = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "db size query failed");
            DbSizeBytes = null;
        }
    }
}

/// <summary>§14 baseline metrics owned by the Runtime API.</summary>
public sealed class RuntimeApiMetrics(WorkerLoopState state, DbSizeCycle dbSizeCycle, RunPipelineMetrics runMetrics)
    : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("dbSizeBytes", dbSizeCycle.DbSizeBytes);
        foreach (var pair in runMetrics.Collect().Where(p => p.Key != "running" && p.Key != "heartbeatUtc" && p.Key != "lastSuccessfulCycleUtc"))
            yield return pair;
    }
}
