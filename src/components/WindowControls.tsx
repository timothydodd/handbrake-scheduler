import { useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { Copy, Minus, Square, X } from "lucide-react";

export const isWindows = navigator.userAgent.includes("Windows");

/** Min/max/close buttons for the frameless window (Windows only — other
 *  platforms keep native decorations, see tauri.windows.conf.json). */
export default function WindowControls() {
  const [maximized, setMaximized] = useState(false);

  useEffect(() => {
    if (!isWindows) return;
    const win = getCurrentWindow();
    void win.isMaximized().then(setMaximized);
    const unlisten = win.onResized(() => {
      void win.isMaximized().then(setMaximized);
    });
    return () => {
      void unlisten.then((fn) => fn());
    };
  }, []);

  if (!isWindows) return null;
  const win = getCurrentWindow();

  return (
    <div className="flex shrink-0 self-stretch">
      <button
        onClick={() => void win.minimize()}
        title="Minimize"
        className="flex w-11 items-center justify-center text-content-muted hover:bg-muted hover:text-content"
      >
        <Minus size={14} />
      </button>
      <button
        onClick={() => void win.toggleMaximize()}
        title={maximized ? "Restore" : "Maximize"}
        className="flex w-11 items-center justify-center text-content-muted hover:bg-muted hover:text-content"
      >
        {maximized ? <Copy size={12} className="-scale-x-100" /> : <Square size={12} />}
      </button>
      <button
        onClick={() => void win.close()}
        title="Close"
        className="flex w-11 items-center justify-center text-content-muted hover:bg-critical hover:text-on-accent"
      >
        <X size={15} />
      </button>
    </div>
  );
}
