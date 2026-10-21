import { create } from "zustand";
import { listen } from "@tauri-apps/api/event";
import * as ipc from "../lib/ipc";
import { confirmDestructive } from "../lib/dialog";
import type { EngineStatus, Job, LogLine, Settings } from "../lib/types";

const LOG_KEEP = 800;

interface AppState {
  settings: Settings | null;
  status: EngineStatus | null;
  /** The queue, oldest first, as the engine keeps it. */
  jobs: Job[];
  log: LogLine[];
  init: () => Promise<void>;
  saveSettings: (s: Settings) => Promise<Settings>;
}

let initialized = false;

export const useAppStore = create<AppState>((set) => ({
  settings: null,
  status: null,
  jobs: [],
  log: [],

  init: async () => {
    if (initialized) return;
    initialized = true;
    await Promise.all([
      listen<Job[]>("jobs:changed", (e) => set({ jobs: e.payload })),
      listen<Job>("job:progress", (e) => set((s) => ({ jobs: s.jobs.map((j) => (j.id === e.payload.id ? e.payload : j)) }))),
      listen<EngineStatus>("status:changed", (e) => set({ status: e.payload })),
      listen<LogLine>("log:line", (e) => set((s) => ({ log: [...s.log, e.payload].slice(-LOG_KEEP) }))),
      // The window's close button while an encode is running.
      listen("app:close-requested", async () => {
        const ok = await confirmDestructive(
          "An encode is running. Quit anyway? It's stopped now and picked up again next time the app runs.",
          "Quit HandbrakeScheduler?",
        );
        if (ok) await ipc.quitApp();
      }),
    ]);
    const [settings, status, jobs, log] = await Promise.all([ipc.getSettings(), ipc.getStatus(), ipc.listJobs(), ipc.getLog()]);
    set({ settings, status, jobs, log });
  },

  saveSettings: async (s) => {
    const saved = await ipc.saveSettings(s);
    set({ settings: saved });
    return saved;
  },
}));

export function isOpen(j: Job): boolean {
  return j.status === "pending" || j.status === "processing";
}
