/**
 * The Bridge's one task prompt, rendered from the run input (pointers v2 —
 * docs/run-worker.md "Run input (pointers v2)"). ADR-0006: agents read GitHub
 * themselves, so the renderer emits ONLY the pointers — identity, trigger and
 * branch pins; no rich sections (conversation, reviews, threads, artifacts,
 * work-item body) exist anymore. The renderer invents nothing: pointers absent
 * from the input are skipped.
 */

/** The follow-up prompt the Bridge sends after a settle that left the completion call unobserved
 * (docs/flows/tools.md completion rule: every Run ends with `report_completion`). */
export function completionNudgePrompt(completionTool) {
  return [
    "Your run cannot be completed yet: the completion call was not confirmed.",
    `Call the ${completionTool} tool ONCE right before finishing — it is the parameterless`,
    'machine-readable "work is done" signal every Run ends with. It publishes nothing; never',
    "call it for work you did not finish. If finishing is impossible, say so clearly in your",
    "final message.",
  ].join("\n");
}

export function renderPrompt(input) {
  const lines = ["# Task"];
  if (isPointer(input?.owner) && isPointer(input?.repo) && isPointer(input?.itemNumber))
    lines.push(`- Issue: ${input.owner}/${input.repo}#${input.itemNumber}`);
  if (isPointer(input?.prNumber)) lines.push(`- PR: #${input.prNumber}`);
  if (isPointer(input?.planCommentId)) lines.push(`- Plan comment: #${input.planCommentId}`);
  if (typeof input?.command === "string" && input.command.trim().length > 0) {
    lines.push("- Command:");
    lines.push(...indented(input.command));
  }
  if (input?.profile) lines.push(`- Profile: ${input.profile}`);
  if (input?.agentBranch) lines.push(`- Agent branch: ${input.agentBranch}`);
  if (input?.defaultBranch) lines.push(`- Default branch: ${input.defaultBranch}`);
  if (input?.reviewHeadSha) lines.push(`- Review head SHA: ${input.reviewHeadSha}`);
  return lines.join("\n");
}

/** A pointer is present when it carries a usable identity (non-empty string / finite number). */
function isPointer(value) {
  if (typeof value === "string") return value.trim().length > 0;
  return typeof value === "number" && Number.isFinite(value);
}

/** Indents every line of a multi-line value by two spaces (no truncation). */
function indented(text) {
  return String(text).replace(/\r/g, "").split("\n").map((line) => `  ${line}`);
}
