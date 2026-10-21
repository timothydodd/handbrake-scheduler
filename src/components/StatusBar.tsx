import { useEffect, useState } from "react";
import { AlertTriangle, Globe, PauseCircle } from "lucide-react";
import { useAppStore } from "../stores/app";
import { useNavStore } from "../stores/nav";
import { fileName, fmtUntil, PHASE_LABEL } from "../lib/format";

/** Bottom bar: what the engine is doing right now, the server, and setup problems. */
export default function StatusBar() {
  const status = useAppStore((s) => s.status);
  const jobs = useAppStore((s) => s.jobs);
  const go = useNavStore((s) => s.go);
  const [, tick] = useState(0);
  // Countdowns ("next scan in 4 min") need a clock.
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 10_000);
    return () => clearInterval(t);
  }, []);

  if (!status) return <footer className="h-7 shrink-0 border-t border-border-light bg-surface" />;
  const current = status.currentJob ? jobs.find((j) => j.id === status.currentJob) : null;
  const idle = status.state === "idle";
  const dot = status.state === "encoding" ? "animate-pulse bg-accent" : status.state === "paused" ? "bg-warning" : status.state === "scanning" ? "animate-pulse bg-info" : "bg-good";
  const label = {
    idle: "Idle",
    scanning: "Scanning folders",
    encoding: "Encoding",
    waitingForWindow: status.windowOpensMs ? `Waiting for the window (opens ${fmtUntil(status.windowOpensMs)})` : "Waiting for the window",
    paused: "Paused",
  }[status.state];
  const problem = status.problems[0] ?? (status.handbrakeVersion ? null : "HandBrakeCLI not found: check its path");

  return (
    <footer className="flex h-7 shrink-0 items-center gap-4 border-t border-border-light bg-surface px-3 text-[11px] text-content-muted">
      <button className="flex items-center gap-1.5 hover:text-content" onClick={() => go("queue")}>
        <span className={`h-2 w-2 rounded-full ${dot}`} />
        <span className="text-content-secondary">{label}</span>
        {status.state === "paused" && <PauseCircle size={12} />}
      </button>
      {current && current.progress && (
        <button className="truncate hover:text-content" onClick={() => go("queue")}>
          {fileName(current.inputPath)} · {PHASE_LABEL[current.progress.phase]} {Math.round(current.progress.fraction * 100)}%
        </button>
      )}
      {idle && status.nextScanMs && <span>Next scan {fmtUntil(status.nextScanMs)}</span>}
      <span className="ml-auto flex items-center gap-1.5">
        <Globe size={12} className={status.server.running ? "text-good" : "text-content-muted"} />
        {status.server.running ? `Uploads on ${status.server.address}` : status.server.error ? "Upload server failed" : "Upload server off"}
      </span>
      {problem && (
        <button className="flex items-center gap-1 text-warning hover:underline" onClick={() => go("settings")}>
          <AlertTriangle size={12} />
          {problem}
        </button>
      )}
    </footer>
  );
}
