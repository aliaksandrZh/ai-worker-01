#!/usr/bin/env node
/**
 * CLI: read an agent.json manifest and emit the pi mcp.json.
 *   node src/generate-mcpjson.mjs <agent.json> [-o mcp.json]
 *       [--bundle <path>] [--manifest-arg <path>] [--github-command <cmd>]
 * Default output is stdout (image build passes -o).
 */
import { readFileSync, writeFileSync } from "node:fs";
import { generateMcpJson } from "./mcpjson.mjs";

function fail(message) {
  console.error(`generate-mcpjson: ${message}`);
  process.exit(1);
}

const argv = process.argv.slice(2);
const manifestPath = argv.find((a) => !a.startsWith("-"));
if (!manifestPath) fail("usage: node src/generate-mcpjson.mjs <agent.json> [-o mcp.json] [--bundle p] [--manifest-arg p] [--github-command c]");

const opt = (flag) => {
  const i = argv.indexOf(flag);
  return i === -1 ? undefined : argv[i + 1];
};

let manifest;
try {
  manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
} catch (e) {
  fail(`cannot read manifest ${manifestPath}: ${e.message}`);
}

let json;
try {
  json = generateMcpJson(manifest, {
    bundle: opt("--bundle"),
    manifestArg: opt("--manifest-arg"),
    githubCommand: opt("--github-command"),
  });
} catch (e) {
  fail(e.message);
}

const out = opt("-o");
const text = JSON.stringify(json, null, 2) + "\n";
if (out) writeFileSync(out, text);
else process.stdout.write(text);
