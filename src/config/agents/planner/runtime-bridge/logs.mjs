/**
 * Per-Run log trio (decision 43): `logs/{stdout.log, stderr.log, bridge.log}` written by the Bridge
 * with the §16 `max_log_size` (default 50 MiB per Run, configurable) applied to the trio's TOTAL:
 * when an append would exceed the shared budget the offending file rotates (`<file>.1` rename, the
 * oldest segment dropped first) and appending continues. A segment is only kept while the budget
 * still holds after it (newest content wins). The session `.jsonl` is NOT part of this cap — it is
 * session storage (decision 57), never log content.
 */
import { appendFileSync, existsSync, readdirSync, renameSync, rmSync, statSync } from "node:fs";
import { join } from "node:path";

const TRIO = ["stdout.log", "stderr.log", "bridge.log"];

export function openLogs(logsDir, maxLogBytes, bootLine) {
  const logs = new Trio(logsDir, maxLogBytes);
  logs.write("bridge.log", bootLine);
  return logs;
}

export class Trio {
  constructor(logsDir, maxLogBytes) {
    this.logsDir = logsDir;
    this.maxLogBytes = maxLogBytes;
  }

  usedBytes() {
    if (!existsSync(this.logsDir)) return 0;
    return readdirSync(this.logsDir)
      .filter((name) => name.endsWith(".log") || name.endsWith(".log.1"))
      .reduce((total, name) => total + statSync(join(this.logsDir, name)).size, 0);
  }

  /** Appends to one trio file; rotates that file when its share or the shared budget would be exceeded. */
  write(fileName, text, { cap = true } = {}) {
    if (!TRIO.includes(fileName)) throw new Error(`not part of the log trio: ${fileName}`);
    if (cap) {
      const share = Math.floor(this.maxLogBytes / 2);
      const file = join(this.logsDir, fileName);
      const size = existsSync(file) ? statSync(file).size : 0;
      const bytes = Buffer.byteLength(text);
      if (size + bytes > share || this.usedBytes() + bytes > this.maxLogBytes)
        this.rotate(fileName);
    }
    appendFileSync(join(this.logsDir, fileName), text);
    this.enforceBudget();
  }

  /** `<file>` → `<file>.1` (dropping the previous segment — newest content wins). */
  rotate(fileName) {
    const segment = join(this.logsDir, `${fileName}.1`);
    if (existsSync(segment)) rmSync(segment);
    const current = join(this.logsDir, fileName);
    if (existsSync(current)) renameSync(current, segment);
    appendFileSync(current, `[rotated ${new Date().toISOString()}] log budget reached (decision 43)\n`);
  }

  /** Budget holds after every write: oldest segments go first; segment-less overshoot is bounded by
   * the single write size and is a diagnosable anomaly (bridge.log reports it on the next rotate). */
  enforceBudget() {
    let overshoot = this.usedBytes() - this.maxLogBytes;
    while (overshoot > 0 && this.dropOldestSegment())
      overshoot = this.usedBytes() - this.maxLogBytes;
  }

  dropOldestSegment() {
    const segments = existsSync(this.logsDir)
      ? readdirSync(this.logsDir).filter((name) => name.endsWith(".log.1")).sort()
      : [];
    if (segments.length === 0) return false;
    rmSync(join(this.logsDir, segments[0]));
    return true;
  }
}
