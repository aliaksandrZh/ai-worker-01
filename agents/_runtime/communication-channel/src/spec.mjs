/**
 * The communication-tool vocabulary (docs/flows/tools.md): the Runtime Bridge's `tool`-route
 * contract. A change to this set or a signature is a bridge contract bump (bridge.json stamp).
 *
 * Payload validation is zod, built from the field spec below so the zod schema and the
 * JSON Schema advertised in tools/list can never drift. Only schema (types/required) is
 * validated here — business rules live on the Orchestrator side. Signatures are hand-synced
 * with the payload records of the .NET adapter (ticket 03); the contract tests catch drift.
 */
import { z } from "zod";

// Kinds drive the success hint:
//  - escalation: the flow parks until a user event resumes it as a NEW Run
//  - completion: report_completion — the run can settle
//  - ack:        short acknowledgment
export const TOOLS = {
  save_plan: {
    kind: "ack",
    description:
      "Save or update the plan: goals, scope, acceptance criteria and steps. The Orchestrator " +
      "publishes the plan as a comment in the issue and runs plan approval. This call publishes " +
      "nothing to GitHub — publication is the Orchestrator's act.",
    fields: [
      { name: "goals", type: "string", required: true },
      { name: "scope", type: "string", required: true },
      { name: "acceptanceCriteria", type: "string", required: true },
      { name: "steps", type: "string[]", required: true },
    ],
  },
  request_clarification: {
    kind: "escalation",
    description:
      "Request missing information from the User via the Orchestrator. The flow PARKS until a " +
      "@bot /resume command resumes it as a NEW Run. This call publishes nothing to GitHub — " +
      "publication is the Orchestrator's act.",
    fields: [
      { name: "question", type: "string", required: true },
      { name: "context", type: "string", required: false },
    ],
  },
  report_blocker: {
    kind: "escalation",
    description:
      "Report a blocker you cannot resolve independently: cause, context and possible solutions. " +
      "The flow PARKS until an @bot /resume command resumes it as a NEW Run. This call publishes " +
      "nothing to GitHub — publication is the Orchestrator's act.",
    fields: [
      { name: "description", type: "string", required: true },
      { name: "cause", type: "string", required: true },
      { name: "options", type: "string[]", required: true },
    ],
  },
  submit_review_comments: {
    kind: "ack",
    description:
      "Submit review findings. The Orchestrator publishes them to the PR and assigns the fixes to " +
      "the Coder (a fixing Run). This call publishes nothing to GitHub — publication is the " +
      "Orchestrator's act.",
    fields: [
      {
        name: "comments",
        type: "object[]",
        required: true,
        fields: [
          { name: "file", type: "string", required: true },
          { name: "line", type: "number", required: true },
          { name: "severity", type: "string", required: true },
          { name: "message", type: "string", required: true },
        ],
      },
    ],
  },
  request_decision: {
    kind: "escalation",
    description:
      "Escalate a review issue that cannot be resolved without user input: issue, context and " +
      "possible solutions. The flow PARKS until a user_decision event resumes it as a NEW Run. " +
      "This call publishes nothing to GitHub — publication is the Orchestrator's act.",
    fields: [
      { name: "issue", type: "string", required: true },
      { name: "context", type: "string", required: true },
      { name: "options", type: "string[]", required: true },
    ],
  },
  report_bug: {
    kind: "ack",
    description:
      "Report a product bug, test-code issue or missing tests: summary, reproduction steps and " +
      "test results. The Orchestrator publishes the report as a comment in the issue and returns " +
      "control to Development. This call publishes nothing to GitHub — publication is the " +
      "Orchestrator's act.",
    fields: [
      { name: "summary", type: "string", required: true },
      { name: "reproSteps", type: "string[]", required: true },
      { name: "testResults", type: "string", required: true },
    ],
  },
  report_env_issue: {
    kind: "escalation",
    description:
      "Report an environment or access problem blocking test execution: what happened and what is " +
      "needed. The flow PARKS until an @bot /resume command (after the User restores the environment) " +
      "resumes it as a NEW Run. This call publishes nothing to GitHub — publication is the " +
      "Orchestrator's act.",
    fields: [
      { name: "description", type: "string", required: true },
      { name: "whatIsNeeded", type: "string", required: true },
    ],
  },
  request_criteria_clarification: {
    kind: "escalation",
    description:
      "Request clarification of acceptance criteria / expected behavior; the Orchestrator routes " +
      "the answer to the Planner. The flow PARKS until a user event resumes it as a NEW Run. " +
      "This call publishes nothing to GitHub — publication is the Orchestrator's act.",
    fields: [
      { name: "question", type: "string", required: true },
      { name: "context", type: "string", required: false },
    ],
  },
  report_tests_passed: {
    kind: "ack",
    description:
      "Report that all tests passed, with a summary; the Orchestrator then checks the manual " +
      "acceptance policy. This call publishes nothing to GitHub — publication is the " +
      "Orchestrator's act.",
    fields: [{ name: "summary", type: "string", required: true }],
  },
  report_completion: {
    kind: "completion",
    description:
      "Explicit, parameterless signal that your work is done — the MANDATORY final call of every " +
      "Run; a Run that ends without it has failed. Payloads travel through the specific tools; " +
      "this call only marks the end. It publishes nothing to GitHub — publication is the " +
      "Orchestrator's act.",
    fields: [],
  },
  submit_pr_description: {
    kind: "ack",
    description:
      "Carry your result to the Orchestrator: the title and body of the Pull Request the " +
      "Orchestrator creates after report_completion. You never write to GitHub. This call " +
      "publishes nothing — publication is the Orchestrator's act.",
    fields: [
      { name: "title", type: "string", required: true },
      { name: "body", type: "string", required: true },
    ],
  },
};

export const VOCABULARY = Object.freeze(new Set(Object.keys(TOOLS)));

// The escalation pattern of docs/flows/tools.md: what happened, context, options -> the flow
// resumes on a user event as a NEW Run.
export const ESCALATION_TOOLS = Object.freeze(
  new Set(Object.entries(TOOLS).filter(([, t]) => t.kind === "escalation").map(([n]) => n)),
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
