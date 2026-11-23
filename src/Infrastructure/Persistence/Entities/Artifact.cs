namespace PiAgent.Infrastructure.Persistence;

/// <summary>Artifact types; supported: `plan`, `test_cases`, `review_report`, `implementation_summary`, `log_reference`.</summary>
public static class ArtifactTypes
{
    public const string Plan = "plan";
    public const string TestCases = "test_cases";
    public const string ReviewReport = "review_report";
    public const string ImplementationSummary = "implementation_summary";
    public const string LogReference = "log_reference";
    public const string Custom = "custom";
}

/// <summary>Supported formats: markdown, json, text — `file_reference` is future.</summary>
public static class ArtifactFormats
{
    public const string Markdown = "markdown";
    public const string Json = "json";
    public const string Text = "text";
}

/// <summary>
/// Explicit handoff between agents. Immutable after creation; content stored inline in
/// PostgreSQL (≤256 KiB per artifact). Created by the Runtime API from validated Run
/// manifests — only the table ships here, as the `TaskArtifactInput` FK target.
/// Plan Artifacts carry no approval status: approval is expressed by the `/code` command that consumes
/// the latest successful plan.
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
