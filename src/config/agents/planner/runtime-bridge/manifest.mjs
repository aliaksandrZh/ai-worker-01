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

/** The structured Markdown prompt the Bridge renders from the frozen input (decision 63, one RPC prompt). */
export function renderPrompt(input) {
  const lines = [];
  lines.push("# Task");
  if (input?.workItem) {
    lines.push(`- Repository: ${input.workItem.repository}`);
    lines.push(`- Number: ${input.workItem.number}`);
    if (input.workItem.title) lines.push(`- Title: ${input.workItem.title}`);
  }
  if (input?.command) lines.push(`- Command: ${input.command}`);
  if (input?.instructions) lines.push("", "## Instructions", input.instructions);
  if (Array.isArray(input?.artifactReferences) && input.artifactReferences.length > 0)
    lines.push("", `## Input artifact references: ${input.artifactReferences.join(", ")}`);
  return lines.join("\n");
}
