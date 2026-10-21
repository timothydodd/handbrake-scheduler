import WindowControls, { isWindows } from "./WindowControls";

/** Custom title bar for the frameless Windows build: app identity on the
 *  left, window controls on the right, whole bar draggable. Other platforms
 *  keep native decorations and never render this. */
export default function TitleBar() {
  if (!isWindows) return null;

  return (
    <div data-tauri-drag-region className="flex h-8 shrink-0 items-center justify-between border-b border-border bg-surface">
      <div data-tauri-drag-region className="flex items-center gap-2 pl-3">
        <img src="/icon.png" alt="" className="pointer-events-none h-4 w-4" />
        <span className="pointer-events-none text-xs font-semibold tracking-wide text-content-secondary">handbrake scheduler</span>
      </div>
      <WindowControls />
    </div>
  );
}
