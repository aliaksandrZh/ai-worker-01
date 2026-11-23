namespace PiAgent.Infrastructure.Persistence;

/// <summary>Why a prior Artifact is supplied to a Task (e.g. `approved_plan`).</summary>
public static class ArtifactInputPurposes
{
    public const string ApprovedPlan = "approved_plan";
}

/// <summary>
/// Join table describing which prior Artifacts are supplied to a Task; unique
/// `(task_id, artifact_id)`. Created at Task admission (`/code` links the approved plan); the Context
/// Assembler consumes the linked artifacts and `/retry` reuses them unchanged.
/// </summary>
public class TaskArtifactInput
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid ArtifactId { get; set; }
    public string Purpose { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
