using PiAgent.OutboxWorker.GitHub;
namespace PiAgent.OutboxWorker;

/// <summary>Outbox Worker operational options; currently the one §16 knob this host reads directly (plan ruling 5).</summary>
public sealed record OutboxWorkerOptions(TimeSpan RetryWindow)
{
    public static TimeSpan DefaultRetryWindow { get; } = TimeSpan.FromHours(24);
}
