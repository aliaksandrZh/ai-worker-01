using PiAgent.Events;
using PiAgent.RunWorker;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// Provisioning wipes the run tree's spool (docs/run-session-id.md "Instance-scoped callbacks",
/// fix B): a reprovision means the previous attempt never settled, and its undelivered entries —
/// especially pre-session-id ones with no sessionId in the body — must never reach the Bridge.
/// </summary>
public sealed class RunTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"runtree-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_fresh_tree_gets_empty_spool_session_and_logs()
    {
        var runDir = RunTree.Ensure(_root, runId: 7, containerUserId: 1000, MinimalInput());

        Assert.True(Directory.Exists(Path.Combine(runDir, "spool")));
        Assert.Empty(Directory.GetFiles(Path.Combine(runDir, "spool")));
        Assert.True(File.Exists(Path.Combine(runDir, "input.json")));
    }

    [Fact]
    public void Reprovisioning_wipes_stale_spool_entries_and_the_terminal_marker()
    {
        var runDir = RunTree.Ensure(_root, runId: 7, containerUserId: 1000, MinimalInput());
        var spool = Path.Combine(runDir, "spool");
        File.WriteAllText(Path.Combine(spool, "000001-evt-6-cancelled.json"), """{"id":"evt-6-cancelled","route":"cancelled"}""");
        File.WriteAllText(Path.Combine(spool, ".terminal-delivered"), "");

        RunTree.Ensure(_root, runId: 7, containerUserId: 1000, MinimalInput());

        Assert.Empty(Directory.GetFiles(spool)); // the ghost terminal is gone before the Bridge boots
    }

    [Fact]
    public void The_workspace_is_kept_across_reprovisions()
    {
        var runDir = RunTree.Ensure(_root, runId: 7, containerUserId: 1000, MinimalInput());
        var keep = Path.Combine(runDir, "workspace", "keep.txt");
        File.WriteAllText(keep, "clone artifacts");

        RunTree.Ensure(_root, runId: 7, containerUserId: 1000, MinimalInput());

        Assert.True(File.Exists(keep)); // only the spool is wiped — the workspace is retry-reuse
    }

    private static RunInput MinimalInput() => new(
        Owner: "o", Repo: "r", ItemNumber: 27, PrNumber: null, Command: "plan",
        PlanCommentId: 0, Profile: "planner", AgentBranch: null, DefaultBranch: "main",
        ReviewHeadSha: null, SessionId: "abc123");
}
