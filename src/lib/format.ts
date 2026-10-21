import type { JobPhase, JobStatus } from "./types";

/** Seconds → "2h 16m" / "12m 05s" / "45s". */
export function fmtDuration(secs: number | null | undefined): string {
  if (secs == null) return "—";
  const h = Math.floor(secs / 3600);
  const m = Math.floor((secs % 3600) / 60);
  const s = Math.floor(secs % 60);
  if (h) return `${h}h ${String(m).padStart(2, "0")}m`;
  if (m) return `${m}m ${String(s).padStart(2, "0")}s`;
  return `${s}s`;
}

/** Bytes → "29.3 GB" in binary units. */
export function fmtSize(bytes: number | null | undefined): string {
  if (bytes == null) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = bytes;
  let i = 0;
  while (Math.round(v) >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v >= 99.95 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`;
}

/** Bytes → whole gigabytes for size fields; "" when unset (0). */
export function bytesToGb(bytes: number): string {
  return bytes ? String(Math.round((bytes / 1024 ** 3) * 100) / 100) : "";
}

export function gbToBytes(text: string): number {
  const n = Number(text);
  return Number.isFinite(n) && n > 0 ? Math.round(n * 1024 ** 3) : 0;
}

export function fmtTime(ms: number): string {
  return new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function fmtWhen(ms: number | null | undefined): string {
  if (ms == null) return "—";
  const d = new Date(ms);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  return sameDay
    ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : d.toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

/** "in 4 min" / "in 2 h 10 min" for a future timestamp. */
export function fmtUntil(ms: number): string {
  const secs = Math.max(0, Math.round((ms - Date.now()) / 1000));
  if (secs < 60) return `in ${secs}s`;
  const m = Math.round(secs / 60);
  if (m < 60) return `in ${m} min`;
  return `in ${Math.floor(m / 60)} h ${String(m % 60).padStart(2, "0")} min`;
}

export function fileName(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

export const STATUS_LABEL: Record<JobStatus, string> = {
  pending: "Queued",
  processing: "Encoding",
  completed: "Done",
  failed: "Failed",
  cancelled: "Cancelled",
};

export const PHASE_LABEL: Record<JobPhase, string> = {
  waiting: "Starting",
  staging: "Copying to staging",
  encoding: "Encoding",
  delivering: "Copying to output",
};

/** "1920×1080" or "". */
export function fmtResolution(w: number | null, h: number | null): string {
  return w && h ? `${w}×${h}` : "";
}
