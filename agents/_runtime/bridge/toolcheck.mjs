/**
 * Publication toolcheck: after `agent_settled` the Bridge verifies the mandated publication kinds
 * against the OBSERVED event stream (tool names with an error-free completed `tool_execution_end`) —
 * the agent never self-reports, there is no result manifest. A missing mandate gets a follow-up
 * prompt (the Bridge nudges, at most PI_PUBLICATION_NUDGES times); a permission-denied mandate fails
 * immediately. On success the Bridge collects the `artifacts/` directory by convention (no agent-side
 * JSON), derives branch/commitSha via Git, and builds the terminal `completed` callback body.
 */
import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, statSync } from "node:fs";
import { basename, extname, join } from "node:path";

const MAX_ARTIFACT_BYTES = 256 * 1024; // max_artifact_size, hard limit

/** Mandated publication kind → the GitHub MCP tool(s) whose successful execution confirms it. */
export const PUBLICATION_TOOLS = {
  issue_comment: ["add_issue_comment"],
  pull_request: ["create_pull_request"],
  review: ["pull_request_review_write"],
};

/** MCP tool records report the server-prefixed name (`mcp__github__add_issue_comment`) while the
 * mandate kinds map the bare GitHub MCP tool name. The normalized name is the last `__` segment. */
export function normalizeToolName(name) {
  return typeof name === "string" && name.includes("__") ? name.split("__").pop() : name;
}

/**
 * Verifies every mandated publication kind against the observed tool stream.
 * `completedToolNames`: tool names of error-free completed `tool_execution_end` records.
 * `deniedToolNames`: tool names the unattended permission policy denied — a mandated publication
 * whose tool was DENIED is a fundamentally different failure from a transport/GitHub one: fail the
 * run with `required_permission_denied` immediately (a nudge cannot help).
 * @returns {ok: true} when every mandated kind is confirmed, else {ok: false, missing, denied}.
 */
export function checkPublications(requiredKinds, completedToolNames = new Set(), deniedToolNames = new Set()) {
  const completed = new Set([...completedToolNames].map(normalizeToolName));
  const deniedNames = new Set([...deniedToolNames].map(normalizeToolName));
  const missing = [];
  const denied = [];
  for (const kind of requiredKinds) {
    const tools = PUBLICATION_TOOLS[kind] ?? [];
    if (tools.some((tool) => completed.has(tool))) continue;
    if (tools.some((tool) => deniedNames.has(tool))) denied.push(kind);
    else missing.push(kind);
  }
  return missing.length === 0 && denied.length === 0
    ? { ok: true, missing: [], denied: [] }
    : { ok: false, missing, denied };
}

/** The follow-up prompt the Bridge sends after a settle that left a mandated publication unconfirmed. */
export function nudgePrompt(missing) {
  return [
    "Your run cannot be completed yet: the mandatory publication(s) were not confirmed:",
    `${missing.join(", ")}.`,
    "",
    "First CHECK on GitHub (issue_read / pull_request_read) whether your earlier publication already",
    "landed. If it did — do NOT post anything new and do not post a \"confirmation entry\": the",
    "confirmation is automatic once the publishing tool call has succeeded again or its execution was",
    "observed; simply finish your work. Only publish now if the publication truly never happened.",
    "Never duplicate a comment, pull request or review. If a publication is impossible, say so",
    "clearly in your final message.",
  ].join("\n");
}

/**
 * Collects the run's artifacts by convention: every file in `<workspace>/artifacts/` is an artifact.
 * The type comes from the file stem (plan.md → `plan`, implementation-summary.md →
 * `implementation_summary`, review.md → `review_report`, test-cases → `test_cases`, anything else →
 * `custom`); the format from the extension (.md → markdown, .json → json, else text). No agent-side
 * JSON ever describes them. Oversize artifacts fail the run — no silent truncation, ever.
 * @returns {artifacts, error} — error: {code, message} | null.
 */
export function collectArtifacts(workspace) {
  const dir = join(workspace, "artifacts");
  if (!existsSync(dir)) return { artifacts: [], error: null };
  const artifacts = [];
  for (const name of readdirSync(dir).sort()) {
    const path = join(dir, name);
    if (!statSync(path).isFile()) continue;
    const bytes = statSync(path).size;
    if (bytes > MAX_ARTIFACT_BYTES)
      return {
        artifacts: [],
        error: { code: "artifact_too_large", message: `artifact 'artifacts/${name}' is ${bytes} bytes; the limit is ${MAX_ARTIFACT_BYTES}` },
      };
    const ext = extname(name).toLowerCase();
    const stem = basename(name, ext).toLowerCase();
    artifacts.push({
      type: ARTIFACT_TYPE_BY_STEM.get(stem) ?? "custom",
      format: FORMAT_BY_EXT.get(ext) ?? "text",
      path: `artifacts/${name}`,
    });
  }
  return { artifacts, error: null };
}

const ARTIFACT_TYPE_BY_STEM = new Map([
  ["plan", "plan"],
  ["implementation-summary", "implementation_summary"],
  ["review", "review_report"],
  ["review-report", "review_report"],
  ["test-cases", "test_cases"],
]);
const FORMAT_BY_EXT = new Map([
  [".md", "markdown"],
  [".json", "json"],
]);

/** Builds the terminal `completed` body (the publication toolcheck precedes it).
 * `sessionFilePath` is the per-Run path the Bridge discovered at launch (`get_state.sessionFile`,
 * carried so the Runtime API can record the actual path on the AgentSession row). */
export function completedTerminal(runId, sessionFilePath, summary, artifacts, facts) {
  return {
    id: `evt-${runId}-terminal`,
    route: "completed",
    body: {
      eventId: `evt-${runId}-terminal`,
      occurredAt: new Date().toISOString(),
      ...(sessionFilePath ? { sessionFilePath } : {}),
      result: {
        summary,
        artifacts,
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

/**
 * The structured Markdown prompt the Bridge renders from the frozen input (one RPC
 * prompt). The input is the context package; sections are optional — the
 * renderer prints only what the Orchestrator assembled and invents nothing: each section maps 1:1
 * to the frozen RunContextSnapshot content.
 */
export function renderPrompt(input) {
  const lines = [];
  const section = (title) => lines.push("", `## ${title}`);

  lines.push("# Task");
  if (input?.profile) lines.push(`- Profile: ${input.profile.name} v${input.profile.version}`);
  if (input?.task) {
    lines.push(`- Task type: ${input.task.type}`);
    if (input.task.targetHeadSha) lines.push(`- Target head SHA: ${input.task.targetHeadSha}`);
    if (input.task.targetBaseSha) lines.push(`- Target base SHA: ${input.task.targetBaseSha}`);
  }
  if (input?.repository)
    lines.push(`- Repository: ${input.repository.fullName} (default branch: ${input.repository.defaultBranch})`);
  if (input?.workItem) {
    lines.push(
      `- Work item: ${input.workItem.type} ${input.workItem.repository}#${input.workItem.number} (${input.workItem.state})`,
      `- Title: ${input.workItem.title}`);
    if (input.workItem.labels?.length) lines.push(`- Labels: ${input.workItem.labels.join(", ")}`);
    if (input.workItem.author) lines.push(`- Author: ${input.workItem.author}`);
    if (input.workItem.body) section("Work item body"), lines.push(input.workItem.body);
  }
  if (input?.instructions) section("Instructions"), lines.push(input.instructions);
  if (input?.commandComment) {
    section("User command");
    lines.push(`${input.commandComment.author} wrote at ${input.commandComment.createdAt} (comment ${input.commandComment.githubCommentId}):`);
    lines.push("", "```text", input.commandComment.text.replace(/\r/g, ""), "```");
  } else if (input?.command) {
    lines.push(`- Command: ${input.command}`);
  }
  if (input?.git) {
    section("Workspace git facts");
    if (input.git.branch) lines.push(`- Agent branch: ${input.git.branch}`);
    if (input.git.baseSha) lines.push(`- Base SHA: ${input.git.baseSha}`);
    if (input.git.currentSha) lines.push(`- Current SHA: ${input.git.currentSha}`);
  }
  if (Array.isArray(input?.artifacts) && input.artifacts.length > 0) {
    section("Input artifacts");
    for (const artifact of input.artifacts) {
      const purpose = artifact.purpose ? ` — purpose: ${artifact.purpose}` : "";
      const reference = artifact.reference ? ` [reference: ${artifact.reference}]` : "";
      lines.push("", `### Artifact ${artifact.type}${purpose}${reference}`);
      if (artifact.content) lines.push(artifact.content.replace(/\r/g, ""));
    }
  }
  if (Array.isArray(input?.reviews) && input.reviews.length > 0) {
    section("Submitted reviews (newest first)");
    for (const review of input.reviews) {
      lines.push(`- ${review.author} at ${review.submittedAt ?? "?"} — state: ${review.state}`);
      if (review.body) lines.push("  ```text", ...indented(review.body), "  ```");
    }
  }
  if (Array.isArray(input?.threads) && input.threads.length > 0) {
    section("Review threads (newest first)");
    for (const thread of input.threads) {
      const binding = [thread.path, thread.line].filter(Boolean).join(":");
      lines.push(
        `- Thread ${thread.githubCommentId}${binding ? ` (at ${binding})` : ""}` +
        `${thread.state ? ` — review state: ${thread.state}` : ""}` +
        (thread.resolved === true ? " — resolved" : thread.resolved === false ? "" : " — resolution unknown"));
      for (const comment of thread.comments ?? [])
        lines.push(`  - ${comment.author} at ${comment.createdAt} (comment ${comment.githubCommentId}): ${inline(comment.body)}`);
    }
  }
  if (Array.isArray(input?.relatedWorkItems) && input.relatedWorkItems.length > 0) {
    section("Related work items");
    for (const related of input.relatedWorkItems)
      lines.push(
        `- relation ${related.relation} (other: ${related.otherType}): ` +
        `${related.repository}#${related.number} "${related.title}" (${related.state})`);
  }
  if (Array.isArray(input?.conversation) && input.conversation.length > 0) {
    section("Conversation (chronological)");
    for (const comment of input.conversation)
      lines.push(`- ${comment.author} at ${comment.createdAt} (comment ${comment.githubCommentId}): ${inline(comment.body)}`);
  }
  return lines.join("\n");
}

/** Indents every line of a multi-line body by two spaces (no truncation). */
function indented(body) {
  return String(body).replace(/\r/g, "").split("\n").map((line) => "  " + line);
}

/** Collapses newlines so a comment stays on one indented line. */
function inline(body) {
  return String(body ?? "").replace(/\r/g, "").replace(/\n+/g, " ").trim();
}
