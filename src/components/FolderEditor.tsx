import { useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import PathInput from "./PathInput";
import PresetInput from "./PresetInput";
import { Drawer, Field, Toggle } from "./ui";
import { bytesToGb, gbToBytes } from "../lib/format";
import type { FolderSetting, ResolutionPreset } from "../lib/types";

const EMPTY_RULE: ResolutionPreset = { minWidth: 3840, maxWidth: null, minHeight: 2160, maxHeight: null, preset: "", description: "" };

function NumberInput({ value, onChange, className = "w-24" }: { value: number | null; onChange: (v: number | null) => void; className?: string }) {
  return (
    <input
      className={`field tabular-nums ${className}`}
      inputMode="numeric"
      value={value ?? ""}
      placeholder="any"
      onChange={(e) => {
        const digits = e.target.value.replace(/\D/g, "");
        onChange(digits ? Number(digits) : null);
      }}
    />
  );
}

/** Drawer for one watched folder. Edits a draft; Save hands it back. */
export default function FolderEditor({ folder, isNew, onSave, onClose }: { folder: FolderSetting; isNew: boolean; onSave: (f: FolderSetting) => void; onClose: () => void }) {
  const [f, setF] = useState<FolderSetting>(folder);
  const set = <K extends keyof FolderSetting>(k: K, v: FolderSetting[K]) => setF({ ...f, [k]: v });
  const setRule = (i: number, patch: Partial<ResolutionPreset>) => set("resolutionPresets", f.resolutionPresets.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const valid = f.inputPath.trim() && f.outputPath.trim();

  return (
    <Drawer
      title={isNew ? "New watched folder" : f.name || "Watched folder"}
      subtitle="Files found here are encoded with these settings"
      onClose={onClose}
      footer={
        <>
          <button className="btn-ghost" onClick={onClose}>
            Cancel
          </button>
          <button className="btn-primary" disabled={!valid} onClick={() => onSave(f)}>
            {isNew ? "Add folder" : "Apply"}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-5 px-5 py-4">
        <Field label="Name" hint="Just for the lists. Empty = the input folder's name.">
          <input className="field w-full" value={f.name} onChange={(e) => set("name", e.target.value)} placeholder="Movies" />
        </Field>
        <Field label="Watch" hint="Processed files are moved into a _moved folder next to where they were found.">
          <PathInput directory value={f.inputPath} onChange={(v) => set("inputPath", v)} />
        </Field>
        <Field label="Encode into" hint="Subfolders are kept: Watch\A\x.mkv → Encode into\A\x.mp4.">
          <PathInput directory value={f.outputPath} onChange={(v) => set("outputPath", v)} />
        </Field>
        <Field label="Preset" hint="A HandBrake preset name. Empty = the default preset from Settings.">
          <PresetInput value={f.preset} onChange={(v) => set("preset", v)} />
        </Field>
        <Field label="File types" hint="Extensions, separated by spaces or commas.">
          <input
            className="field mono w-full"
            value={f.fileExtensions.join(" ")}
            onChange={(e) => set("fileExtensions", e.target.value.split(/[\s,]+/).filter(Boolean))}
            spellCheck={false}
          />
        </Field>
        <div className="grid grid-cols-2 gap-4">
          <Field label="Skip files under (GB)" hint="Empty = no minimum.">
            <input className="field w-full tabular-nums" value={bytesToGb(f.minFileSizeBytes)} onChange={(e) => set("minFileSizeBytes", gbToBytes(e.target.value))} placeholder="0" />
          </Field>
          <Field label="Skip files over (GB)" hint="Empty = no maximum.">
            <input className="field w-full tabular-nums" value={bytesToGb(f.maxFileSizeBytes)} onChange={(e) => set("maxFileSizeBytes", gbToBytes(e.target.value))} placeholder="∞" />
          </Field>
        </div>
        <Toggle checked={f.recursive} onChange={(v) => set("recursive", v)} label="Look in subfolders too" />
        <Toggle checked={f.deleteSource} onChange={(v) => set("deleteSource", v)} label="Delete the source once the encode succeeded" hint="Otherwise it stays in the _moved folder." />
        <Toggle
          checked={f.useStaging}
          onChange={(v) => set("useStaging", v)}
          label="Encode in a staging folder"
          hint="Copies the source to a local folder first and copies the encode out afterwards. Much faster when the folders are on a NAS."
        />
        {f.useStaging && (
          <Field label="Staging folder" hint="Empty = the staging folder from Settings (or the system temp folder).">
            <PathInput directory value={f.stagingPath} onChange={(v) => set("stagingPath", v)} placeholder="Default" />
          </Field>
        )}

        <section>
          <div className="mb-2 flex items-center justify-between">
            <span className="field-label !mb-0">Preset by resolution</span>
            <button className="btn-chip" onClick={() => set("resolutionPresets", [...f.resolutionPresets, { ...EMPTY_RULE }])}>
              <Plus size={12} />
              Add rule
            </button>
          </div>
          {f.resolutionPresets.length === 0 ? (
            <p className="text-xs text-content-muted">Sources are read before they're queued; a rule picks another preset for sizes that match (4K, for instance).</p>
          ) : (
            <div className="flex flex-col gap-3">
              {f.resolutionPresets.map((r, i) => (
                <div key={i} className="rounded-md border border-border-light p-3">
                  <div className="flex items-end gap-2">
                    <Field label="Width from">
                      <NumberInput value={r.minWidth} onChange={(v) => setRule(i, { minWidth: v ?? 0 })} />
                    </Field>
                    <Field label="to">
                      <NumberInput value={r.maxWidth} onChange={(v) => setRule(i, { maxWidth: v })} />
                    </Field>
                    <Field label="Height from">
                      <NumberInput value={r.minHeight} onChange={(v) => setRule(i, { minHeight: v ?? 0 })} />
                    </Field>
                    <Field label="to">
                      <NumberInput value={r.maxHeight} onChange={(v) => setRule(i, { maxHeight: v })} />
                    </Field>
                    <button className="btn-quiet mb-0.5" onClick={() => set("resolutionPresets", f.resolutionPresets.filter((_, j) => j !== i))} title="Remove rule">
                      <Trash2 size={14} />
                    </button>
                  </div>
                  <div className="mt-2">
                    <Field label="Use preset">
                      <PresetInput value={r.preset} onChange={(v) => setRule(i, { preset: v })} placeholder="HQ 2160p60 4K HEVC Surround" />
                    </Field>
                  </div>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    </Drawer>
  );
}
