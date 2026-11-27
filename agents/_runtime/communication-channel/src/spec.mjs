/**
 * The adapter over the ONE tool catalog (agents/_runtime/tools.json): the communication-tool
 * vocabulary (docs/flows/tools.md §2) — the Runtime Bridge's `tool`-route contract and this
 * server's registration set.
 *
 * Payload validation is zod, built from the field spec of the catalog so the zod schema and the
 * JSON Schema advertised in tools/list can never drift. Only schema (types/required) is
 * validated here — business rules live on the Orchestrator side. Signatures are hand-synced
 * with the payload records of the .NET adapter (ticket 03); the contract tests catch drift.
 */
import { z } from "zod";
import catalogJson from "../../tools.json" with { type: "json" };

export const CATALOG = Object.freeze(catalogJson.tools);

// Kinds drive the success hint:
//  - escalation: the flow parks until a user event resumes it as a NEW Run
//  - completion: report_completion — the run can settle
//  - ack:        short acknowledgment
const communicationEntries = Object.entries(CATALOG)
  .filter(([, spec]) => spec.carrier === "communication");

export const TOOLS = Object.freeze(Object.fromEntries(
  communicationEntries.map(([name, spec]) => [name, { ...spec, name }]),
));

export const VOCABULARY = Object.freeze(new Set(Object.keys(TOOLS)));

/** The catalog's carrier classification: only the `communication` carrier's calls ride the
 * route-tool wire and register here; `github-mcp` entries are the GitHub MCP server's context
 * tools, `none` is declared-but-unimplemented (web_search). */
export const isCommunicationTool = (name) => CATALOG[name]?.carrier === "communication";

// The escalation pattern of docs/flows/tools.md: what happened, context, options -> the flow
// resumes on a user event as a NEW Run.
export const ESCALATION_TOOLS = Object.freeze(
  new Set(communicationEntries.filter(([, t]) => t.kind === "escalation").map(([n]) => n)),
);

const SCALARS = {
  string: z.string(),
  number: z.number(),
};

function zodField(f) {
  if (f.type === "string[]") return z.array(z.string());
  if (f.type === "object[]") {
    const shape = {};
    for (const sub of f.fields) shape[sub.name] = zodField(sub);
    const item = z.strictObject(shape);
    return f.required ? z.array(item) : z.array(item).optional();
  }
  const t = SCALARS[f.type];
  return f.required ? t : t.optional();
}

/** zod schema for a tool's arguments, built from its spec (strict: unknown keys rejected).
 *
 * `guard` — an optional () => string? workspace check (null = ok, string = the failure reason).
 * It attaches to the delivery tools of the agent-branch carve-out (the coder): their validity
 * depends on workspace git state the agent does not pass as arguments, so the check is an
 * argument-free refinement — the failure flows through the SAME describeIssue path as any
 * field error, surfacing to the agent as an isError tool result it can fix in-run.
 */
const GUARDED_TOOLS = Object.freeze(new Set(["report_completion", "submit_pr_description"]));

export function zodSchema(toolSpec, guard) {
  const shape = {};
  for (const f of toolSpec.fields) shape[f.name] = zodField(f);
  const base = z.strictObject(shape);
  if (guard && GUARDED_TOOLS.has(toolSpec.name ?? "")) {
    return base.superRefine((_args, ctx) => {
      const problem = guard();
      if (problem) ctx.addIssue({ code: "custom", path: ["workspace"], message: problem });
    });
  }
  return base;
}

/** JSON Schema for tools/list, derived from the same spec so both stay in sync. */
export function jsonSchema(toolSpec) {
  const properties = {};
  const required = [];
  for (const f of toolSpec.fields) {
    if (f.type === "string[]") {
      properties[f.name] = { type: "array", items: { type: "string" } };
    } else if (f.type === "object[]") {
      const itemProps = {};
      const itemRequired = [];
      for (const sub of f.fields) {
        itemProps[sub.name] = { type: sub.type };
        if (sub.required) itemRequired.push(sub.name);
      }
      properties[f.name] = {
        type: "array",
        items: { type: "object", properties: itemProps, required: itemRequired, additionalProperties: false },
      };
    } else {
      properties[f.name] = { type: f.type };
    }
    if (f.required) required.push(f.name);
  }
  return { type: "object", properties, required, additionalProperties: false };
}

/** Success hint carried by every valid call (the agent reads it as the tool result text). */
export function hint(name) {
  const kind = TOOLS[name].kind;
  if (kind === "escalation")
    return (
      `Recorded: ${name}. The flow is PARKED until a user event resumes it as a NEW Run — ` +
      "you will get freshly assembled context then. You must still end THIS Run with report_completion."
    );
  if (kind === "completion")
    return "Recorded: report_completion. The run can settle.";
  return `Recorded: ${name}. This call published nothing to GitHub — publication is the Orchestrator's act; the payload travels with the event.`;
}
