/**
 * Durable callback spool: every callback is written to the per-Run `callback-spool/`
 * directory on the persistent tree BEFORE its first send, with:
 *  - events/terminal appended as ordered JSON entries (`000001-<id>.json`; the terminal id is
 *    deterministic so a redelivered terminal can never duplicate);
 *  - heartbeats coalesced to the latest value in a single slot (`heartbeat.json` — an un-delivered
 *    older beat is replaced on the sender side);
 *  - delivery completing only when the Runtime API answers 2xx — then the entry is removed; a
 *    delivered terminal writes the `.terminal-delivered` marker;
 *  - restart redelivery: `next()` re-serves pending entries in order, same eventIds.
 */
import { existsSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const BEAT_SLOT = "heartbeat.json";

export function openSpool(spoolDir, options = {}) {
  const onTerminalDelivered = options.onTerminalDelivered ?? (() => {});
  let sequence = 0;
  let serving = null;

  function listPending() {
    if (!existsSync(spoolDir)) return [];
    return readdirSync(spoolDir)
      .filter((name) => name.endsWith(".json") && name !== BEAT_SLOT)
      .sort()
      .map((name) => join(spoolDir, name));
  }

  function load(path) {
    try {
      return { path, ...JSON.parse(readFileSync(path, "utf8")) };
    } catch {
      renameSync(path, `${path}.corrupt`);
      return null; // unreadable entries never block the queue; they are parked for the operator
    }
  }

  return {
    /** Appends one entry: `{id, route, body}` — the body is durably spooled BEFORE its first send. */
    append(entry) {
      if (sequence === 0) sequence = listPending().length;
      sequence += 1;
      const name = `${String(sequence).padStart(6, "0")}-${entry.id}.json`;
      writeFileSync(join(spoolDir, name), JSON.stringify({ id: entry.id, route: entry.route, body: entry.body }));
    },

    /** Single-slot coalescing heartbeat: replaces any un-delivered previous beat. */
    coalescingHeartbeats(heartbeatId) {
      const slot = join(spoolDir, BEAT_SLOT);
      return ({ occurredAt }) => {
        writeFileSync(slot, JSON.stringify({
          id: heartbeatId,
          route: "heartbeat",
          body: { eventId: heartbeatId, occurredAt },
        }));
      };
    },

    /** Next pending entry in order: the coalesced beat first, then the ordered entries. Null when empty. */
    async next() {
      if (serving) return serving; // the sender resolves one entry at a time
      const beat = join(spoolDir, BEAT_SLOT);
      if (existsSync(beat)) {
        const entry = load(beat);
        if (entry) {
          serving = entry;
          return serving;
        }
      }
      const pending = listPending().map(load).filter(Boolean);
      if (pending.length === 0) return null;
      serving = pending[0];
      return serving;
    },

    /** 2xx answered → the callback is durably delivered; the entry leaves the spool. */
    remove(entry) {
      rmSync(entry.path, { force: true });
      // terminal routes (incl. the `cancelled`) write the done marker
      if (entry.route === "completed" || entry.route === "failed" || entry.route === "cancelled") onTerminalDelivered();
      if (serving === entry) serving = null;
    },

    /** Operator-diagnosable parking (permanent 4xx the terminal fallback cannot cover). */
    reject(entry, reason) {
      renameSync(entry.path, `${entry.path}.rejected`);
      if (serving === entry) serving = null;
    },

    count() {
      return listPending().length + (existsSync(join(spoolDir, BEAT_SLOT)) ? 1 : 0);
    },

    /** Test/inspection helper: pending entry bodies in delivery order. */
    snapshot() {
      const beat = existsSync(join(spoolDir, BEAT_SLOT)) ? { ...load(join(spoolDir, BEAT_SLOT)), path: join(spoolDir, BEAT_SLOT) } : null;
      const pending = listPending().map(load).filter(Boolean);
      return { beat, pending };
    },
  };
}
