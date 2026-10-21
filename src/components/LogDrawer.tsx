import { useEffect, useRef } from "react";
import { Drawer, EmptyState } from "./ui";
import { useAppStore } from "../stores/app";
import { useNavStore } from "../stores/nav";
import { fmtTime } from "../lib/format";

/** The engine's messages, newest at the bottom. */
export default function LogDrawer() {
  const log = useAppStore((s) => s.log);
  const setLog = useNavStore((s) => s.setLog);
  const end = useRef<HTMLDivElement>(null);
  // Braces matter: newer WebView2 builds can return a Promise from scrollIntoView, and
  // React would take a returned value for a cleanup function.
  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" });
  }, [log.length]);

  return (
    <Drawer title="Log" subtitle="Scans, encodes and uploads, this session (also written to the logs folder)" onClose={() => setLog(false)}>
      {log.length === 0 ? (
        <EmptyState title="Nothing yet">Messages appear here as folders are scanned and files are encoded.</EmptyState>
      ) : (
        <div className="mono px-4 py-3 leading-relaxed">
          {log.map((l, i) => (
            <div key={i} className={`flex gap-3 ${l.error ? "text-critical" : "text-content-secondary"}`}>
              <span className="shrink-0 text-content-muted">{fmtTime(l.tsMs)}</span>
              <span className="w-28 shrink-0 truncate text-content-muted" title={l.source}>
                {l.source}
              </span>
              <span className="min-w-0 break-words">{l.text}</span>
            </div>
          ))}
          <div ref={end} />
        </div>
      )}
    </Drawer>
  );
}
