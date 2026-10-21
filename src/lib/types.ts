// Mirrors the Rust DTOs (serde camelCase). Keep in sync by hand with
// crates/hbs-core (settings, job, handbrake) and crates/hbs-engine (engine).

export interface ResolutionPreset {
  minWidth: number;
  maxWidth: number | null;
  minHeight: number;
  maxHeight: number | null;
  preset: string;
  description: string;
}

export interface FolderSetting {
  name: string;
  inputPath: string;
  outputPath: string;
  preset: string;
  /** Lower-case with the dot: ".mkv". */
  fileExtensions: string[];
  deleteSource: boolean;
  recursive: boolean;
  useStaging: boolean;
  stagingPath: string;
  minFileSizeBytes: number;
  /** 0 = no limit. */
  maxFileSizeBytes: number;
  resolutionPresets: ResolutionPreset[];
}

export interface Settings {
  handbrakePath: string;
  defaultPreset: string;
  folders: FolderSetting[];
  monitoringEnabled: boolean;
  scanIntervalMinutes: number;
  /** "22:00"; both empty = always. */
  startTime: string;
  endTime: string;
  keepJobLogs: boolean;
  serverEnabled: boolean;
  listenAddress: string;
  incomingDir: string;
  maxUploadBytes: number;
  stagingDir: string;
  maxAttempts: number;
}

export type JobStatus = "pending" | "processing" | "completed" | "failed" | "cancelled";
export type JobSource = "folder" | "upload" | "manual";
export type JobPhase = "waiting" | "staging" | "encoding" | "delivering";

export interface JobProgress {
  phase: JobPhase;
  fraction: number;
  fps: number | null;
  avgFps: number | null;
  etaSecs: number | null;
}

export interface Job {
  id: string;
  source: JobSource;
  inputPath: string;
  originalPath: string;
  folderName: string;
  outputDir: string;
  preset: string;
  deleteSource: boolean;
  stagingDir: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdMs: number;
  startedMs: number | null;
  finishedMs: number | null;
  status: JobStatus;
  attempts: number;
  error: string | null;
  outputFile: string | null;
  /** Left out by the backend while idle. */
  progress?: JobProgress;
}

export type EngineState = "idle" | "scanning" | "encoding" | "waitingForWindow" | "paused";

export interface ServerStatus {
  running: boolean;
  address: string;
  error: string | null;
}

export interface EngineStatus {
  state: EngineState;
  paused: boolean;
  currentJob: string | null;
  nextScanMs: number | null;
  windowOpensMs: number | null;
  window: string | null;
  pending: number;
  server: ServerStatus;
  handbrakeVersion: string | null;
  problems: string[];
}

/** `GET /files` entries (the .NET PascalCase contract, kept for AutoMk). */
export interface ReceivedFile {
  OriginalFileName: string;
  StoredFileName: string;
  FilePath: string;
  FileSizeBytes: number;
  ReceivedTimestamp: string;
  RelativeFilePath: string;
}

export interface LogLine {
  tsMs: number;
  source: string;
  text: string;
  error: boolean;
}
