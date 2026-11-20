namespace PiAgent.Infrastructure.Persistence;

/// <summary>Why a prior Artifact is supplied to a Task (02 §10a example: `approved_plan`).</summary>
public static class ArtifactInputPurposes
{
    public const string ApprovedPlan = "approved_plan";
}

/// <summary>
/// Join table describing which prior Artifacts are supplied to a Task (02 §10a); unique
/// `(task_id, artifact_id)`. Created at Task admission (`/code` links the approved plan); the Context
/// Assembler (ticket 11) consumes the linked artifacts and `/retry` (ticket 15) reuses them unchanged.
/// </summary>
public class TaskArtifactInput
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid ArtifactId { get; set; }
    public string Purpose { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
