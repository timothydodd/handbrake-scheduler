import { useMemo, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { FilePlus2, FolderOpen, ListVideo, Pause, Play, RefreshCw, RotateCcw, Trash2, X } from "lucide-react";
import { EmptyState, PageHeader, ProgressBar, Spinner, StatusBadge, errText } from "../components/ui";
import * as ipc from "../lib/ipc";
import { fileName, fmtDuration, fmtResolution, fmtSize, fmtWhen, PHASE_LABEL } from "../lib/format";
import type { Job } from "../lib/types";
import { isOpen, useAppStore } from "../stores/app";
import { useNavStore } from "../stores/nav";
import { toast } from "../stores/toast";

const VIDEO_EXTENSIONS = ["mkv", "mp4", "m4v", "avi", "mov", "ts", "m2ts", "wmv"];

const SOURCE_LABEL = { folder: "Watched folder", upload: "Upload", manual: "Added by hand" };

function run(p: Promise<unknown>) {
  p.catch((e) => toast.error(errText(e)));
}

/** The encode that's running right now, with its phase and speed. */
function CurrentJob({ job }: { job: Job }) {
  const p = job.progress ?? { phase: "waiting" as const, fraction: 0, fps: null, avgFps: null, etaSecs: null };
  const encoding = p.phase === "encoding";
  return (
    <section className="card mx-6 mt-5 p-5">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="card-title mb-1">Now</div>
          <div className="truncate text-base font-semibold text-content" title={job.inputPath}>
            {fileName(job.inputPath)}
          </div>
          <div className="mt-0.5 text-xs text-content-muted">
            {job.preset}
            {fmtResolution(job.width, job.height) && ` · ${fmtResolution(job.width, job.height)}`} · {fmtSize(job.sizeBytes)} · {job.folderName}
            {job.attempts > 1 && ` · try ${job.attempts}`}
          </div>
        </div>
        <button className="btn-ghost" onClick={() => run(ipc.cancelJob(job.id))}>
          <X size={14} />
          Cancel
        </button>
      </div>
      <div className="mt-4 flex items-center justify-between text-xs">
        <span className="text-content-secondary">{PHASE_LABEL[p.phase]}</span>
        <span className="tabular-nums text-content-muted">
          {encoding && p.avgFps != null && `${p.avgFps.toFixed(1)} fps avg · `}
          {encoding && p.etaSecs != null && `${fmtDuration(p.etaSecs)} left · `}
          {Math.round(p.fraction * 100)}%
        </span>
      </div>
      <ProgressBar value={p.phase === "waiting" ? null : p.fraction} className="mt-1.5" />
      <div className="mt-2 truncate text-[11px] text-content-muted" title={job.outputDir}>
        → {job.outputDir}
      </div>
    </section>
  );
}

function JobRow({ job }: { job: Job }) {
  const openable = job.status === "completed" && job.outputFile;
  return (
    <tr>
      <td className="!whitespace-normal">
        <div className="truncate text-content" title={job.inputPath}>
          {fileName(job.inputPath)}
        </div>
        {job.error && <div className="mt-0.5 break-words text-[11px] text-critical">{job.error}</div>}
      </td>
      <td>
        <StatusBadge status={job.status} />
      </td>
      <td className="text-content-secondary">{job.preset}</td>
      <td className="tabular-nums text-content-secondary">{fmtSize(job.sizeBytes)}</td>
      <td className="text-content-muted" title={SOURCE_LABEL[job.source]}>
        {job.folderName}
      </td>
      <td className="tabular-nums text-content-muted">{fmtWhen(job.finishedMs ?? job.createdMs)}</td>
      <td>
        <div className="flex justify-end gap-1">
          {job.status === "pending" && (
            <button className="btn-chip" onClick={() => run(ipc.cancelJob(job.id))} title="Take it out of the queue">
              <X size={12} />
              Cancel
            </button>
          )}
          {openable && (
            <button className="btn-chip" onClick={() => run(ipc.openFolder(job.outputFile!))} title="Show the encode">
              <FolderOpen size={12} />
            </button>
          )}
          {(job.status === "failed" || job.status === "cancelled") && (
            <button className="btn-chip" onClick={() => run(ipc.retryJob(job.id))} title="Queue it again">
              <RotateCcw size={12} />
              Retry
            </button>
          )}
          {!isOpen(job) && (
            <button className="btn-chip" onClick={() => run(ipc.removeJob(job.id))} title="Forget this job">
              <Trash2 size={12} />
            </button>
          )}
        </div>
      </td>
    </tr>
  );
}

export default function QueuePage() {
  const jobs = useAppStore((s) => s.jobs);
  const status = useAppStore((s) => s.status);
  const settings = useAppStore((s) => s.settings);
  const go = useNavStore((s) => s.go);
  const [adding, setAdding] = useState(false);

  const current = useMemo(() => jobs.find((j) => j.status === "processing"), [jobs]);
  const pending = useMemo(() => jobs.filter((j) => j.status === "pending"), [jobs]);
  const finished = useMemo(() => jobs.filter((j) => !isOpen(j)).reverse(), [jobs]);
  const paused = status?.paused ?? false;

  const addFiles = async () => {
    const picked = await open({ multiple: true, filters: [{ name: "Video", extensions: VIDEO_EXTENSIONS }] });
    const paths = Array.isArray(picked) ? picked : typeof picked === "string" ? [picked] : [];
    if (paths.length === 0) return;
    setAdding(true);
    try {
      const added = await ipc.addFiles(paths, null);
      toast.success(`Queued ${added.length} file${added.length === 1 ? "" : "s"}.`);
    } catch (e) {
      toast.error(errText(e));
    } finally {
      setAdding(false);
    }
  };

  const subtitle = status
    ? {
        idle: pending.length ? "Ready to encode" : "Nothing to do",
        scanning: "Scanning the watched folders…",
        encoding: `${pending.length} more waiting`,
        waitingForWindow: `Encodes run ${status.window ?? "in the window"}; ${pending.length} waiting`,
        paused: "Paused: nothing starts until you resume",
      }[status.state]
    : "";

  return (
    <div className="flex min-h-full flex-col">
      <PageHeader title="Queue" subtitle={subtitle}>
        <button className="btn-ghost" onClick={() => run(ipc.scanNow())} title="Look in the watched folders now">
          <RefreshCw size={14} className={status?.state === "scanning" ? "animate-spin" : ""} />
          Scan now
        </button>
        <button className="btn-ghost" onClick={() => void addFiles()} disabled={adding || !settings?.folders.length} title={settings?.folders.length ? "Queue files from anywhere" : "Add a watched folder first"}>
          {adding ? <Spinner size={14} /> : <FilePlus2 size={14} />}
          Add files
        </button>
        <button className={paused ? "btn-primary" : "btn-ghost"} onClick={() => run(ipc.setPaused(!paused))}>
          {paused ? <Play size={14} /> : <Pause size={14} />}
          {paused ? "Resume" : "Pause"}
        </button>
      </PageHeader>

      {current && <CurrentJob job={current} />}

      {jobs.length === 0 ? (
        <EmptyState icon={<ListVideo size={36} />} title="The queue is empty">
          {settings?.folders.length ? (
            <>Files that appear in the watched folders are queued at the next scan. Or press Add files.</>
          ) : (
            <>
              Add a{" "}
              <button className="text-accent hover:underline" onClick={() => go("folders")}>
                watched folder
              </button>{" "}
              to get started.
            </>
          )}
        </EmptyState>
      ) : (
        <div className="flex flex-col gap-6 px-6 py-5">
          {pending.length > 0 && (
            <section>
              <div className="card-title mb-2">Waiting ({pending.length})</div>
              <div className="card overflow-hidden">
                <table className="table">
                  <thead>
                    <tr>
                      <th className="w-full">File</th>
                      <th>Status</th>
                      <th>Preset</th>
                      <th>Size</th>
                      <th>Folder</th>
                      <th>Added</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {pending.map((j) => (
                      <JobRow key={j.id} job={j} />
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
          {finished.length > 0 && (
            <section>
              <div className="mb-2 flex items-center justify-between">
                <div className="card-title">Finished ({finished.length})</div>
                <button className="btn-chip" onClick={() => run(ipc.clearFinished())}>
                  <Trash2 size={12} />
                  Clear
                </button>
              </div>
              <div className="card overflow-hidden">
                <table className="table">
                  <thead>
                    <tr>
                      <th className="w-full">File</th>
                      <th>Status</th>
                      <th>Preset</th>
                      <th>Size</th>
                      <th>Folder</th>
                      <th>When</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {finished.map((j) => (
                      <JobRow key={j.id} job={j} />
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </div>
      )}
    </div>
  );
}
