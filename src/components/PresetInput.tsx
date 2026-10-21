import { useEffect, useState } from "react";
import { RefreshCw } from "lucide-react";
import * as ipc from "../lib/ipc";
import { useAppStore } from "../stores/app";
import { errText } from "./ui";

let cached: string[] | null = null;
let cachedFor = "";

/** Preset name with HandBrake's own list as suggestions (fetched once per CLI path). */
export default function PresetInput({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) {
  const cli = useAppStore((s) => s.settings?.handbrakePath ?? "");
  const [presets, setPresets] = useState<string[]>(cached && cachedFor === cli ? cached : []);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async (force = false) => {
    if (!cli || (!force && cached && cachedFor === cli)) return;
    setBusy(true);
    setError(null);
    try {
      cached = await ipc.listPresets(cli);
      cachedFor = cli;
      setPresets(cached);
    } catch (e) {
      setError(errText(e));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cli]);

  return (
    <div>
      <div className="flex gap-2">
        <input className="field flex-1" list="hbs-presets" value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder ?? "HQ 1080p30 Surround"} spellCheck={false} />
        <button className="btn-ghost" type="button" onClick={() => void load(true)} disabled={busy || !cli} title="Ask HandBrakeCLI for its presets again">
          <RefreshCw size={14} className={busy ? "animate-spin" : ""} />
        </button>
      </div>
      <datalist id="hbs-presets">
        {presets.map((p) => (
          <option key={p} value={p} />
        ))}
      </datalist>
      {error && <span className="mt-1 block text-[11px] text-critical">{error}</span>}
    </div>
  );
}
