using PiAgent.OutboxWorker.GitHub;
namespace PiAgent.OutboxWorker;

/// <summary>Outbox Worker operational options; currently the one knob this host reads directly.</summary>
public sealed record OutboxWorkerOptions(TimeSpan RetryWindow)
{
    public static TimeSpan DefaultRetryWindow { get; } = TimeSpan.FromHours(24);
}
