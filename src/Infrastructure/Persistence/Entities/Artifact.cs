namespace PiAgent.Infrastructure.Persistence;

/// <summary>Artifact types (02 §17); V1 supports `plan`, `test_cases`, `review_report`, `implementation_summary`, `log_reference`.</summary>
public static class ArtifactTypes
{
    public const string Plan = "plan";
    public const string TestCases = "test_cases";
    public const string ReviewReport = "review_report";
    public const string ImplementationSummary = "implementation_summary";
    public const string LogReference = "log_reference";
    public const string Custom = "custom";
}

/// <summary>Supported formats in V1 (decision 32): markdown, json, text — `file_reference` is future.</summary>
public static class ArtifactFormats
{
    public const string Markdown = "markdown";
    public const string Json = "json";
    public const string Text = "text";
}

/// <summary>
/// Explicit handoff between agents (02 §17). Immutable after creation; content stored inline in
/// PostgreSQL (≤256 KiB per artifact, decision 32). Created by the Runtime API from validated Run
/// manifests (ticket 09) — this ticket only ships the table as the `TaskArtifactInput` FK target.
/// Plan Artifacts carry no approval status: approval is expressed by the `/code` command that consumes
/// the latest successful plan (decision 80).
/// </summary>
public class Artifact
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Type { get; set; } = null!;
    public string Format { get; set; } = null!;
    public string? Content { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}
