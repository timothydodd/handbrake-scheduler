import { useEffect, useRef, type ReactNode } from "react";
import { AlertOctagon, CheckCircle2, Info, Loader2, X } from "lucide-react";
import { useToastStore } from "../stores/toast";
import { isWindows } from "./WindowControls";
import type { JobStatus } from "../lib/types";
import { STATUS_LABEL } from "../lib/format";

// --- progress -----------------------------------------------------------------

/** Progress bar: accent fill on a lighter step of the same hue. `null` = indeterminate. */
export function ProgressBar({ value, tone = "accent", className = "" }: { value: number | null; tone?: "accent" | "good" | "critical"; className?: string }) {
  const color = tone === "good" ? "var(--good)" : tone === "critical" ? "var(--critical)" : "var(--accent)";
  const v = value == null ? null : Math.max(0, Math.min(1, value)) * 100;
  return (
    <div
      className={`h-1.5 w-full overflow-hidden rounded-full ${className}`}
      style={{ backgroundColor: `color-mix(in srgb, ${color} 18%, transparent)` }}
      role="progressbar"
      aria-valuenow={v ?? undefined}
      aria-valuemin={0}
      aria-valuemax={100}
    >
      {v == null ? (
        <div className="h-full w-1/3 animate-pulse rounded-full" style={{ backgroundColor: color }} />
      ) : (
        <div className="h-full rounded-full transition-[width] duration-300" style={{ width: `${v}%`, backgroundColor: color }} />
      )}
    </div>
  );
}

// --- badges -------------------------------------------------------------------

const STATUS_TINT: Record<JobStatus, string> = {
  pending: "tint-info",
  processing: "bg-accent text-on-accent",
  completed: "tint-good",
  failed: "tint-critical",
  cancelled: "tint-muted",
};

export function StatusBadge({ status }: { status: JobStatus }) {
  return (
    <span className={`inline-flex items-center whitespace-nowrap rounded px-1.5 py-0.5 text-[11px] font-semibold ${STATUS_TINT[status]}`}>
      {STATUS_LABEL[status]}
    </span>
  );
}

// --- layout bits --------------------------------------------------------------

export function PageHeader({ title, subtitle, children }: { title: ReactNode; subtitle?: ReactNode; children?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-end justify-between gap-3 border-b border-border-light px-6 pb-3 pt-5">
      <div className="min-w-0">
        <h1 className="truncate text-lg font-semibold text-content">{title}</h1>
        {subtitle && <p className="mt-0.5 text-xs text-content-muted">{subtitle}</p>}
      </div>
      {children && <div className="flex flex-wrap items-center gap-2">{children}</div>}
    </div>
  );
}

export function EmptyState({ icon, title, children }: { icon?: ReactNode; title: string; children?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-16 text-center">
      {icon && <div className="text-content-muted">{icon}</div>}
      <div className="text-sm font-medium text-content">{title}</div>
      {children && <div className="max-w-md text-xs text-content-muted">{children}</div>}
    </div>
  );
}

export function Spinner({ size = 16 }: { size?: number }) {
  return <Loader2 size={size} className="animate-spin text-content-muted" />;
}

/** Labelled form row. */
export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="block">
      <span className="field-label">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[11px] text-content-muted">{hint}</span>}
    </label>
  );
}

export function Toggle({ checked, onChange, label, hint }: { checked: boolean; onChange: (v: boolean) => void; label: string; hint?: ReactNode }) {
  return (
    <label className="flex cursor-pointer items-start gap-2.5">
      <input type="checkbox" className="accent-brand mt-0.5" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>
        <span className="block text-sm text-content">{label}</span>
        {hint && <span className="block text-xs text-content-muted">{hint}</span>}
      </span>
    </label>
  );
}

export function Section({ title, children, actions }: { title: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <section className="card p-5">
      <div className="mb-4 flex items-center justify-between gap-2">
        <h2 className="card-title">{title}</h2>
        {actions}
      </div>
      <div className="flex flex-col gap-4">{children}</div>
    </section>
  );
}

// --- overlays -----------------------------------------------------------------

// Open overlays in the order they opened. Esc goes to the last one only.
const escStack: { current: (() => void) | null }[] = [];
let escListening = false;

function useEscape(onClose: () => void, enabled = true) {
  const handler = useRef<(() => void) | null>(null);
  handler.current = enabled ? onClose : null;
  useEffect(() => {
    if (!escListening) {
      escListening = true;
      window.addEventListener("keydown", (e) => e.key === "Escape" && escStack[escStack.length - 1]?.current?.());
    }
    escStack.push(handler);
    return () => void escStack.splice(escStack.indexOf(handler), 1);
  }, []);
}

export function Drawer({ title, subtitle, onClose, children, actions, footer }: { title: ReactNode; subtitle?: ReactNode; onClose: () => void; children: ReactNode; actions?: ReactNode; footer?: ReactNode }) {
  useEscape(onClose);
  return (
    // Starts below the custom title bar, so the window can still be moved, minimized and closed.
    <div className={`fixed inset-x-0 bottom-0 z-40 flex justify-end ${isWindows ? "top-8" : "top-0"}`} onMouseDown={onClose}>
      <div className="absolute inset-0 bg-black/30" />
      <aside className="relative flex h-full w-[min(760px,92vw)] flex-col border-l border-border bg-surface shadow-[var(--shadow-md)]" onMouseDown={(e) => e.stopPropagation()}>
        <header className="flex items-start justify-between gap-3 border-b border-border-light px-5 py-3">
          <div className="min-w-0">
            <div className="truncate text-base font-semibold text-content">{title}</div>
            {subtitle && <div className="mt-0.5 truncate text-xs text-content-muted">{subtitle}</div>}
          </div>
          <div className="flex shrink-0 items-center gap-2">
            {actions}
            <button className="btn-quiet" onClick={onClose} title="Close (Esc)">
              <X size={16} />
            </button>
          </div>
        </header>
        <div className="min-h-0 flex-1 overflow-auto">{children}</div>
        {footer && <footer className="flex items-center justify-end gap-2 border-t border-border-light px-5 py-3">{footer}</footer>}
      </aside>
    </div>
  );
}

export function Toasts() {
  const { toasts, dismiss } = useToastStore();
  return (
    <div className="pointer-events-none fixed bottom-10 right-4 z-[60] flex w-80 flex-col gap-2">
      {toasts.map((t) => {
        const Icon = t.kind === "success" ? CheckCircle2 : t.kind === "error" ? AlertOctagon : Info;
        const color = t.kind === "success" ? "text-good" : t.kind === "error" ? "text-critical" : "text-info";
        return (
          <div key={t.id} className="card pointer-events-auto flex items-start gap-2 px-3 py-2.5 text-sm shadow-[var(--shadow-md)]">
            <Icon size={16} className={`mt-0.5 shrink-0 ${color}`} />
            <span className="min-w-0 flex-1 break-words text-content">{t.message}</span>
            <button className="text-content-muted hover:text-content" title="Dismiss" aria-label="Dismiss" onClick={() => dismiss(t.id)}>
              <X size={14} />
            </button>
          </div>
        );
      })}
    </div>
  );
}

/** Rejected IPC promises carry the Rust error string. */
export function errText(e: unknown): string {
  return typeof e === "string" ? e : e instanceof Error ? e.message : String(e);
}
