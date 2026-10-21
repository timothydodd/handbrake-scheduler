import { create } from "zustand";

export type Page = "queue" | "folders" | "settings";

interface NavState {
  page: Page;
  logOpen: boolean;
  /** Asked before leaving the current page (unsaved settings); true = fine to go. */
  leaveGuard: (() => Promise<boolean>) | null;
  setLeaveGuard: (g: (() => Promise<boolean>) | null) => void;
  go: (page: Page) => void;
  setLog: (open: boolean) => void;
}

export const useNavStore = create<NavState>((set, get) => ({
  page: "queue",
  logOpen: false,
  leaveGuard: null,
  setLeaveGuard: (leaveGuard) => set({ leaveGuard }),
  go: (page) => {
    const guard = get().leaveGuard;
    if (!guard || page === get().page) return set({ page });
    void guard().then((ok) => ok && set({ page, leaveGuard: null }));
  },
  setLog: (logOpen) => set({ logOpen }),
}));
