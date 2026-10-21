import { create } from "zustand";

export type ThemePref = "light" | "dark" | "system";

const STORAGE_KEY = "hbs:theme";

function systemDark(): boolean {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? true;
}

function resolve(pref: ThemePref): "light" | "dark" {
  return pref === "system" ? (systemDark() ? "dark" : "light") : pref;
}

/** Stamp the resolved theme onto <html> so the CSS tokens switch. */
function apply(pref: ThemePref) {
  document.documentElement.setAttribute("data-theme", resolve(pref));
}

function readPref(): ThemePref {
  try {
    return (localStorage.getItem(STORAGE_KEY) as ThemePref | null) ?? "dark";
  } catch {
    return "dark";
  }
}

interface ThemeState {
  pref: ThemePref;
  resolved: "light" | "dark";
  setPref: (pref: ThemePref) => void;
}

const initial = readPref();
apply(initial);

window.matchMedia?.("(prefers-color-scheme: dark)").addEventListener("change", () => {
  const { pref } = useThemeStore.getState();
  if (pref === "system") {
    apply("system");
    useThemeStore.setState({ resolved: resolve("system") });
  }
});

export const useThemeStore = create<ThemeState>((set) => ({
  pref: initial,
  resolved: resolve(initial),
  setPref: (pref) => {
    try {
      localStorage.setItem(STORAGE_KEY, pref);
    } catch {
      /* private mode etc. — theme just won't persist */
    }
    apply(pref);
    set({ pref, resolved: resolve(pref) });
  },
}));
