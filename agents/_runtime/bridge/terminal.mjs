/**
 * The terminal `completed` callback body (docs/run-worker.md): summary (the last aggregated
 * assistant_output), git facts (branch/commitSha — the pointer producers for
 * RunCompleted.Branch/CommitSha) and the discovered session file path. The artifacts/
 * convention and the review outcome are gone with the old mandates; the worker parses the
 * git facts tolerantly from `result`.
 */
import { execFileSync } from "node:child_process";

export function completedTerminal(runId, sessionFilePath, summary, facts) {
  return {
    id: `evt-${runId}-terminal`,
    route: "completed",
    body: {
      eventId: `evt-${runId}-terminal`,
      occurredAt: new Date().toISOString(),
      ...(sessionFilePath ? { sessionFilePath } : {}),
      result: {
        summary,
        branch: facts.branch,
        commitSha: facts.commitSha,
      },
    },
  };
}

export function gitFacts(workspace) {
  const facts = {};
  try {
    facts.branch = execFileSync("git", ["-C", workspace, "rev-parse", "--abbrev-ref", "HEAD"], { encoding: "utf8" }).trim();
  } catch {
    facts.branch = null; // no repository (or no commits) in the workspace
  }
  try {
    facts.commitSha = execFileSync("git", ["-C", workspace, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
  } catch {
    facts.commitSha = null;
  }
  return facts;
}
