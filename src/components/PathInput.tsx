import { open } from "@tauri-apps/plugin-dialog";
import { FolderOpen } from "lucide-react";
import { isWindows } from "./WindowControls";

/** Text field with a Browse button (folder or program picker). */
export default function PathInput({ value, onChange, directory, placeholder }: { value: string; onChange: (v: string) => void; directory?: boolean; placeholder?: string }) {
  const browse = async () => {
    const picked = await open({
      directory: !!directory,
      defaultPath: value || undefined,
      filters: !directory && isWindows ? [{ name: "Programs", extensions: ["exe"] }] : undefined,
    });
    if (typeof picked === "string") onChange(picked);
  };
  return (
    <div className="flex gap-2">
      <input className="field mono flex-1" value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} spellCheck={false} />
      <button className="btn-ghost" type="button" onClick={() => void browse()}>
        <FolderOpen size={14} />
        Browse
      </button>
    </div>
  );
}
