// Dev-only stand-in for the Tauri backend so the UI can be viewed in a plain
// browser (`npm run dev`, open http://localhost:1420). Loaded by main.tsx only
// when running under Vite dev without a Tauri runtime. Fixture data is fake.

import type { EngineStatus, Job, Settings } from "../lib/types";

type Args = Record<string, unknown>;
const GB = 1024 ** 3;
const now = Date.now();

let settings: Settings = {
  handbrakePath: "C:\\Program Files\\HandBrake\\HandBrakeCLI.exe",
  defaultPreset: "HQ 1080p30 Surround",
  folders: [
    {
      name: "Movies",
      inputPath: "D:\\Incoming\\Movies",
      outputPath: "\\\\nas\\media\\Movies",
      preset: "HQ 1080p30 Surround",
      fileExtensions: [".mkv", ".mp4"],
      deleteSource: true,
      recursive: true,
      useStaging: true,
      stagingPath: "C:\\Temp\\HandbrakeStaging",
      minFileSizeBytes: 0,
      maxFileSizeBytes: 0,
      resolutionPresets: [{ minWidth: 3840, maxWidth: null, minHeight: 2160, maxHeight: null, preset: "HQ 2160p60 4K HEVC Surround", description: "4K" }],
    },
    {
      name: "TV Shows",
      inputPath: "D:\\Incoming\\TV Shows",
      outputPath: "\\\\nas\\media\\TV Shows",
      preset: "HQ 720p30 Surround",
      fileExtensions: [".mkv"],
      deleteSource: false,
      recursive: true,
      useStaging: false,
      stagingPath: "",
      minFileSizeBytes: 100 * 1024 * 1024,
      maxFileSizeBytes: 0,
      resolutionPresets: [],
    },
  ],
  monitoringEnabled: true,
  scanIntervalMinutes: 5,
  startTime: "22:00",
  endTime: "06:00",
  keepJobLogs: false,
  serverEnabled: true,
  listenAddress: "0.0.0.0:5000",
  incomingDir: "C:\\Users\\demo\\Videos\\HandbrakeScheduler\\incoming",
  maxUploadBytes: 100 * GB,
  stagingDir: "",
  maxAttempts: 3,
};

const job = (id: string, name: string, extra: Partial<Job> = {}): Job => ({
  id,
  source: "folder",
  inputPath: `D:\\Incoming\\Movies\\${name}`,
  originalPath: `D:\\Incoming\\Movies\\${name}`,
  folderName: "Movies",
  outputDir: "\\\\nas\\media\\Movies",
  preset: "HQ 1080p30 Surround",
  deleteSource: true,
  stagingDir: "C:\\Temp\\HandbrakeStaging",
  sizeBytes: 24 * GB,
  width: 1920,
  height: 1080,
  createdMs: now - 3600_000,
  startedMs: null,
  finishedMs: null,
  status: "pending",
  attempts: 0,
  error: null,
  outputFile: null,
  ...extra,
});

let jobs: Job[] = [
  job("j1", "Heat (1995).mkv", { status: "completed", finishedMs: now - 7200_000, outputFile: "\\\\nas\\media\\Movies\\Heat (1995).mp4", attempts: 1, startedMs: now - 10_800_000 }),
  job("j2", "Alien (1979).mkv", { status: "failed", attempts: 3, error: "HandBrakeCLI failed (exit code 3): ERROR: avio_open2 failed errno -2", finishedMs: now - 3000_000, inputPath: "D:\\Incoming\\Movies\\_moved\\Alien (1979).mkv" }),
  job("j3", "Dune (2021).mkv", { status: "processing", startedMs: now - 600_000, attempts: 1, width: 3840, height: 2160, preset: "HQ 2160p60 4K HEVC Surround", sizeBytes: 58 * GB, inputPath: "D:\\Incoming\\Movies\\_moved\\Dune (2021).mkv", progress: { phase: "encoding", fraction: 0.31, fps: 24.5, avgFps: 22.1, etaSecs: 5400 } }),
  job("j4", "Friends - S02E01.mkv", { source: "upload", folderName: "Uploads → TV Shows", outputDir: "\\\\nas\\media\\TV Shows\\Friends (1994)\\Season 02", preset: "HQ 720p30 Surround", sizeBytes: 1.2 * GB, stagingDir: "" }),
  job("j5", "Friends - S02E02.mkv", { source: "upload", folderName: "Uploads → TV Shows", outputDir: "\\\\nas\\media\\TV Shows\\Friends (1994)\\Season 02", preset: "HQ 720p30 Surround", sizeBytes: 1.1 * GB, stagingDir: "" }),
];

let status: EngineStatus = {
  state: "encoding",
  paused: false,
  currentJob: "j3",
  nextScanMs: now + 4 * 60_000,
  windowOpensMs: null,
  window: "22:00–06:00",
  pending: 2,
  server: { running: true, address: "0.0.0.0:5000", error: null },
  handbrakeVersion: "HandBrake 1.9.2",
  problems: [],
};

const listeners = new Map<string, number[]>();
const callbacks = new Map<number, (e: unknown) => void>();
function emit(event: string, payload: unknown) {
  for (const id of listeners.get(event) ?? []) callbacks.get(id)?.({ event, id, payload });
}

function refreshStatus() {
  const cur = jobs.find((j) => j.status === "processing");
  status = { ...status, currentJob: cur?.id ?? null, pending: jobs.filter((j) => j.status === "pending").length, state: status.paused ? "paused" : cur ? "encoding" : "idle" };
  emit("status:changed", status);
}

setInterval(() => {
  const cur = jobs.find((j) => j.status === "processing");
  if (!cur || status.paused) return;
  const p = cur.progress ?? { phase: "encoding" as const, fraction: 0, fps: 24, avgFps: 22, etaSecs: 6000 };
  const fraction = Math.min(1, p.fraction + 0.01);
  cur.progress = { ...p, fraction, etaSecs: Math.round((1 - fraction) * 8000) };
  if (fraction >= 1) {
    cur.status = "completed";
    cur.finishedMs = Date.now();
    cur.outputFile = `${cur.outputDir}\\${cur.inputPath.split("\\").pop()!.replace(/\.\w+$/, ".mp4")}`;
    delete cur.progress;
    const next = jobs.find((j) => j.status === "pending");
    if (next) {
      next.status = "processing";
      next.startedMs = Date.now();
      next.attempts = 1;
      next.progress = { phase: "encoding", fraction: 0, fps: 60, avgFps: 58, etaSecs: 900 };
    }
    jobs = [...jobs];
    emit("jobs:changed", jobs);
    emit("log:line", { tsMs: Date.now(), source: "queue", text: `Finished ${cur.inputPath}`, error: false });
    refreshStatus();
  } else {
    emit("job:progress", { ...cur });
  }
}, 700);

const handlers: Record<string, (a: Args) => unknown> = {
  get_settings: () => settings,
  save_settings: (a) => {
    settings = a.settings as Settings;
    return settings;
  },
  test_handbrake: () => "HandBrake 1.9.2",
  list_presets: () => ["Very Fast 1080p30", "Fast 1080p30", "HQ 1080p30 Surround", "HQ 720p30 Surround", "HQ 2160p60 4K HEVC Surround", "H.265 MKV 1080p30"],
  get_status: () => status,
  list_jobs: () => jobs,
  list_received: () => [],
  scan_now: () => emit("log:line", { tsMs: Date.now(), source: "scan", text: "Queued 0 new file(s).", error: false }),
  add_files: (a) => {
    const added = (a.paths as string[]).map((p, i) => job(`m${Date.now()}${i}`, p.split("\\").pop()!, { source: "manual", inputPath: p, originalPath: p, createdMs: Date.now() }));
    jobs = [...jobs, ...added];
    emit("jobs:changed", jobs);
    refreshStatus();
    return added;
  },
  cancel_job: (a) => {
    jobs = jobs.map((j) => (j.id === a.id ? { ...j, status: "cancelled", finishedMs: Date.now(), progress: undefined } : j));
    emit("jobs:changed", jobs);
    refreshStatus();
  },
  retry_job: (a) => {
    const j = jobs.find((x) => x.id === a.id)!;
    jobs = [...jobs.filter((x) => x.id !== a.id), { ...j, status: "pending", attempts: 0, error: null, finishedMs: null }];
    emit("jobs:changed", jobs);
    refreshStatus();
  },
  remove_job: (a) => {
    jobs = jobs.filter((j) => j.id !== a.id);
    emit("jobs:changed", jobs);
  },
  clear_finished: () => {
    jobs = jobs.filter((j) => j.status === "pending" || j.status === "processing");
    emit("jobs:changed", jobs);
  },
  set_paused: (a) => {
    status = { ...status, paused: a.paused as boolean };
    refreshStatus();
  },
  open_folder: (a) => console.info("[mock] open", a.path),
  get_log: () => [
    { tsMs: now - 60_000, source: "server", text: "Listening on http://0.0.0.0:5000 (POST /upload, GET /health).", error: false },
    { tsMs: now - 58_000, source: "scan", text: "Dune (2021).mkv: 3840x2160 (4K), 2:35:10", error: false },
    { tsMs: now - 50_000, source: "handbrake", text: "ERROR: avio_open2 failed errno -2", error: true },
  ],
};

let cbId = 0;
const w = window as unknown as Record<string, unknown>;
w.__TAURI_INTERNALS__ = {
  transformCallback: (cb: (e: unknown) => void) => {
    const id = ++cbId;
    callbacks.set(id, cb);
    return id;
  },
  unregisterCallback: () => undefined,
  invoke: async (cmd: string, args: Args) => {
    if (cmd === "plugin:event|listen") {
      const { event, handler } = args as { event: string; handler: number };
      listeners.set(event, [...(listeners.get(event) ?? []), handler]);
      return handler;
    }
    if (cmd.startsWith("plugin:event|")) return ++cbId;
    if (cmd.startsWith("plugin:opener|")) return null;
    if (cmd === "plugin:dialog|message") return window.confirm(String(args.message)) ? "Ok" : "Cancel";
    if (cmd === "plugin:dialog|open") return (args.options as { multiple?: boolean })?.multiple ? ["C:\\demo\\picked.mkv"] : "C:\\demo\\picked";
    if (cmd.startsWith("plugin:dialog|")) return null;
    const h = handlers[cmd];
    if (!h) {
      console.info("[mock] no-op", cmd, args);
      return null;
    }
    return h(args);
  },
  metadata: { currentWindow: { label: "main" }, currentWebview: { label: "main", windowLabel: "main" } },
};
w.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener: () => undefined };
