namespace PiAgent.Hosts.Tests.Config;

/// <summary>Shared test fixture: the shipping sample config-tree literals (parser, validator, loader tests; mirrored by deploy/config/ + agents/).</summary>
public static class ConfigSamples
{
    public const string System = """
        github:
          bot_login: pi-bot

        poll_interval: 60s
        poll_overlap: 5m
        per_page: 100
        max_concurrent_runs: 10
        startup_timeout: 10m
        idle_timeout: 60m
        max_run_duration: 4h
        heartbeat_interval: 30s
        max_log_size: 50MiB
        max_artifact_size: 256KiB
        max_task_artifacts_size: 512KiB
        max_automatic_retries: 1
        outbox_retry_window: 24h
        orphan_grace_period: 24h
        orphan_quarantine_period: 24h
        """;

    public const string Repositories = """
        repositories: []

        # Add repositories as:
        # repositories:
        #   - owner: example-org
        #     name: example-repo
        #     enabled: true
        #     # allowlist absent = enabled and seeded with the repository owner (decision 49);
        #     # allowlist: [] explicitly empties it, disabling list filtering (write-permission baseline remains).
        #     allowlist: [alice, bob]
        #     permitted_commands: [plan, status, stop, help, retry]
        #     permitted_work_item_types: [issue]
        #     authorization_policy:
        #       minimum_permission: write
        """;

    public const string Routing = """
        # Global default rules (repository_id = NULL); per-repo overrides win, the version is
        # pinned on the Task at creation (02 §12a). Phase 1 ships the planner profile only,
        # so only the plan rule is seeded (decision 36).
        rules:
          - task_type: plan
            profile: planner
            priority: 1
            enabled: true
        # Per-repo override example (repository must exist in repositories.yaml):
        # rules:
        #   - task_type: plan
        #     repository: example-org/example-repo
        #     profile: planner
        """;

    public const string PlannerProfile = """
        description: Analyze requirements and repository; identify gaps; prepare an implementation plan
        enabled: true
        runtime_type: pi
        max_context_tokens: 200000
        execution_limits:
          startup_timeout: 10m
          idle_timeout: 60m
          max_run_duration: 4h
        resources:
          cpu: 2.0
          memory: 2GiB
        retry_policy:
          max_automatic_retries: 1
          initial_backoff: 30s
        bridge:
          version: "1.0.0"
          heartbeat_interval: 15s
          event_cadence: 5s
        system_prompt: |
          You are the planner agent. Analyze the issue, identify gaps and open questions,
          and prepare an implementation plan as the task Artifact.
        secrets: []
        capabilities: []
        """;

    /// <summary>One configured repository + the phase-1 plan rule — the onboarding scenario.</summary>
    public const string RepositoriesOneEntry = """
        repositories:
          - owner: example-org
            name: example-repo
        """;

    public const string RoutingPlanOnly = """
        rules:
          - task_type: plan
            profile: planner
        """;

    /// <summary>Populates a config root with the shipping sample (planner profile included).</summary>
    public static void WriteSampleTree(string root, string system = System, string repositories = Repositories,
        string routing = Routing, string plannerProfile = PlannerProfile)
    {
        File.WriteAllText(Path.Combine(root, "system.yaml"), system);
        File.WriteAllText(Path.Combine(root, "repositories.yaml"), repositories);
        File.WriteAllText(Path.Combine(root, "routing.yaml"), routing);
        Directory.CreateDirectory(Path.Combine(root, "agents", "planner"));
        File.WriteAllText(Path.Combine(root, "agents", "planner", "profile.yaml"), plannerProfile);
    }
}
