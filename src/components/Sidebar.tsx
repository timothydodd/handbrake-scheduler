import { FolderCog, ListVideo, Moon, ScrollText, Settings, Sun, type LucideIcon } from "lucide-react";
import { isOpen, useAppStore } from "../stores/app";
import { useNavStore, type Page } from "../stores/nav";
import { useThemeStore } from "../stores/theme";

export default function Sidebar() {
  const { page, go, setLog } = useNavStore();
  const jobs = useAppStore((s) => s.jobs);
  const status = useAppStore((s) => s.status);
  const { resolved, setPref } = useThemeStore();
  const open = jobs.filter(isOpen).length;
  const encoding = status?.state === "encoding";

  const item = (p: Page, label: string, Icon: LucideIcon, badge?: React.ReactNode) => {
    const active = page === p;
    return (
      <button
        key={p}
        onClick={() => go(p)}
        className={`group flex w-full items-center gap-2.5 rounded-md px-2.5 py-1.5 text-sm transition-colors ${
          active ? "bg-muted font-medium text-content" : "text-content-secondary hover:bg-muted hover:text-content"
        }`}
      >
        <Icon size={16} className={active ? "text-accent" : "text-content-muted group-hover:text-content-secondary"} />
        <span className="flex-1 text-left">{label}</span>
        {badge}
      </button>
    );
  };

  return (
    <nav className="flex w-52 shrink-0 flex-col border-r border-border-light bg-surface px-2 py-3">
      <div className="mb-4 flex items-center gap-2 px-2.5">
        <img src="/icon.png" alt="" className="h-6 w-6" />
        <div className="leading-tight">
          <div className="text-sm font-semibold text-content">HandbrakeScheduler</div>
          <div className="text-[10px] font-medium uppercase tracking-widest text-brand-pink">watch &amp; encode</div>
        </div>
      </div>
      <div className="flex flex-col gap-0.5">
        {item(
          "queue",
          "Queue",
          ListVideo,
          open > 0 && (
            <span
              className={`rounded px-1.5 text-[11px] font-semibold tabular-nums ${encoding ? "bg-accent text-on-accent" : "tint-info"}`}
              title={encoding ? `Encoding, ${open} in the queue` : `${open} queued`}
            >
              {open}
            </span>
          ),
        )}
        {item("folders", "Folders", FolderCog)}
      </div>
      <div className="mt-auto flex flex-col gap-0.5 border-t border-border-light pt-2">
        {item("settings", "Settings", Settings)}
        <button onClick={() => setLog(true)} className="flex w-full items-center gap-2.5 rounded-md px-2.5 py-1.5 text-sm text-content-secondary hover:bg-muted hover:text-content">
          <ScrollText size={16} className="text-content-muted" />
          Log
        </button>
        <button
          onClick={() => setPref(resolved === "dark" ? "light" : "dark")}
          className="flex w-full items-center gap-2.5 rounded-md px-2.5 py-1.5 text-sm text-content-secondary hover:bg-muted hover:text-content"
        >
          {resolved === "dark" ? <Sun size={16} className="text-content-muted" /> : <Moon size={16} className="text-content-muted" />}
          {resolved === "dark" ? "Light theme" : "Dark theme"}
        </button>
      </div>
    </nav>
  );
}
