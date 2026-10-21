import { invoke } from "@tauri-apps/api/core";
import type { EngineStatus, Job, LogLine, ReceivedFile, Settings } from "./types";

// --- settings ---

export const getSettings = () => invoke<Settings>("get_settings");
export const saveSettings = (settings: Settings) => invoke<Settings>("save_settings", { settings });
/** Runs HandBrakeCLI once; resolves to its version line. */
export const testHandbrake = (path: string) => invoke<string>("test_handbrake", { path });
export const listPresets = (path: string) => invoke<string[]>("list_presets", { path });

// --- queue ---

export const getStatus = () => invoke<EngineStatus>("get_status");
export const listJobs = () => invoke<Job[]>("list_jobs");
export const listReceived = () => invoke<ReceivedFile[]>("list_received");
export const scanNow = () => invoke<void>("scan_now");
export const addFiles = (paths: string[], folderIndex: number | null) => invoke<Job[]>("add_files", { paths, folderIndex });
export const cancelJob = (id: string) => invoke<void>("cancel_job", { id });
export const retryJob = (id: string) => invoke<void>("retry_job", { id });
export const removeJob = (id: string) => invoke<void>("remove_job", { id });
export const clearFinished = () => invoke<void>("clear_finished");
export const setPaused = (paused: boolean) => invoke<void>("set_paused", { paused });

// --- misc ---

export const openFolder = (path: string) => invoke<void>("open_folder", { path });
export const getLog = () => invoke<LogLine[]>("get_log");
export const quitApp = () => invoke<void>("quit_app");
