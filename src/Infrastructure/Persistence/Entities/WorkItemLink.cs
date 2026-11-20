namespace PiAgent.Infrastructure.Persistence;

/// <summary>WorkItemLink relation (02 §4). V1 ships `closes` and `related_to` only;
/// `created_from`, `blocks`, `custom` are reserved (decision 79).</summary>
public static class WorkItemLinkRelations
{
    public const string Closes = "closes";
    public const string RelatedTo = "related_to";
}

/// <summary>How a WorkItemLink was formed (02 §4). V1 uses the two GitHub-derived values;
/// `agent_action`/`manual` are future (V1 has no manual link management, decision 51).</summary>
public static class WorkItemLinkSources
{
    public const string GithubClosingReference = "github_closing_reference";
    public const string GithubCrossReference = "github_cross_reference";
}

/// <summary>
/// An explicit relationship between two WorkItems of the same repository (02 §4, decision 51).
/// Stored with typed role columns instead of the conceptual source/target pair (allowed by
/// "names and types can be adapted", 02 preamble; amendment recorded in task 05): the role follows
/// the item's type — the issue side and the PR side are distinct, so no direction convention is
/// needed and self-relations are impossible by construction. V1 writes only Issue↔PR rows; a future
/// Issue↔Issue relation (reserved types, decision 79) would add a second nullable role column.
/// </summary>
public class WorkItemLink
{
    public Guid Id { get; set; }
    /// <summary>The issue side of the link (work_item.type = issue).</summary>
    public Guid IssueWorkItemId { get; set; }
    /// <summary>The PR side of the link (work_item.type = pull_request).</summary>
    public Guid PrWorkItemId { get; set; }
    public string RelationType { get; set; } = null!;
    /// <summary>Provenance — how the link was detected (02 §4 enumeration).</summary>
    public string Source { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
