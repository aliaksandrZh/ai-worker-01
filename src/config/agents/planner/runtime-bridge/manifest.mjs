/**
 * Run result manifest (04 §4, decisions 42/59): after `agent_settled` the agent must have written
 * `result.json` in its writable directory. The Bridge validates the manifest and artifact files in
 * place (within the workspace, ≤256 KiB), correlates `requiredPublications` with the observed
 * `tool_execution_end` records (the decision-42 verification), and builds the terminal callback body
 * (outcome: `completed` | `needs_human_input`); branch/commitSha are derived via Git here.
 */
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, statSync } from "node:fs";
import { join, resolve, sep } from "node:path";

const MAX_ARTIFACT_BYTES = 256 * 1024; // §16 max_artifact_size, hard limit
const FORMAT = new Set(["markdown", "json", "text"]);
const TYPE = new Set(["plan", "test_cases", "review_report", "implementation_summary"]);

export function loadManifest(workspace, log = () => {}) {
  const path = join(workspace, "result.json");
  if (!existsSync(path)) return null;
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    log(`result.json is not valid JSON: ${error.message}\n`);
    return null;
  }
}

/**
 * Validates `{schemaVersion, summary, artifacts[], requiredPublications[], outcome?}`; every artifact
 * file must exist inside the workspace and be ≤256 KiB — no silent truncation, ever.
 * @returns null when valid, else the diagnosable reason string.
 */
export function validateManifest(manifest, workspace, observedCompletedToolCalls = new Set()) {
  if (manifest === null) return "manifest_invalid: the agent did not write a result.json";
  if (manifest.schemaVersion !== 1) return "manifest_invalid: schemaVersion must be 1";
  if (typeof manifest.summary !== "string" || manifest.summary.length === 0)
    return "manifest_invalid: summary is required";
  const artifacts = manifest.artifacts ?? [];
  if (!Array.isArray(artifacts)) return "manifest_invalid: artifacts must be a list";
  const root = resolve(workspace);
  for (const artifact of artifacts) {
    if (!artifact || typeof artifact.type !== "string" || typeof artifact.format !== "string" || typeof artifact.path !== "string")
      return "manifest_invalid: artifacts[] entries need type/format/path";
    if (!TYPE.has(artifact.type)) return `manifest_invalid: artifact type '${artifact.type}' is not valid`;
    if (!FORMAT.has(artifact.format)) return `manifest_invalid: artifact format '${artifact.format}' is not supported`;
    const absolute = resolve(root, artifact.path);
    if (!absolute.startsWith(root + sep)) return `manifest_invalid: artifact path '${artifact.path}' escapes the workspace`;
    if (!existsSync(absolute)) return `manifest_invalid: artifact file '${artifact.path}' does not exist in the workspace`;
    const bytes = statSync(absolute).size;
    if (bytes > MAX_ARTIFACT_BYTES)
      return `artifact_too_large: artifact '${artifact.path}' is ${bytes} bytes; the limit is ${MAX_ARTIFACT_BYTES}`;
  }
  if (manifest.outcome !== undefined && manifest.outcome !== "needs_human_input")
    return "manifest_invalid: outcome must be 'needs_human_input' (or absent)";
  for (const publication of manifest.requiredPublications ?? []) {
    if (!publication || typeof publication.toolCallId !== "string" || typeof publication.kind !== "string")
      return "manifest_invalid: requiredPublications[] entries need toolCallId/kind";
    if (!observedCompletedToolCalls.has(publication.toolCallId))
      return `github_publication_failed: required publication '${publication.toolCallId}' (${publication.kind}) had no completed tool execution`;
  }
  return null;
}

/** Builds the terminal `completed` body (validation, including publication correlation, precedes it). */
export function terminalFromManifest(manifest, runId) {
  return {
    id: `evt-${runId}-terminal`,
    route: "completed",
    body: {
      eventId: `evt-${runId}-terminal`,
      occurredAt: new Date().toISOString(),
      ...(manifest.outcome === "needs_human_input" ? { outcome: "needs_human_input" } : {}),
      result: {
        summary: manifest.summary,
        artifacts: (manifest.artifacts ?? []).map(({ type, format, path }) => ({ type, format, path })),
        ...gitFacts(workspaceOf()),
      },
    },
  };
}

function workspaceOf() {
  return process.env.PI_WORKSPACE || "/workspace";
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
 * The structured Markdown prompt the Bridge renders from the frozen input (decision 63, one RPC
 * prompt). The input is the ticket-11 context package (v2 shape); sections are optional — the
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
