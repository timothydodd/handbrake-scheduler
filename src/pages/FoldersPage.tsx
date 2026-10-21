import { useState } from "react";
import { ArrowRight, FolderCog, FolderOpen, Pencil, Plus, Trash2 } from "lucide-react";
import FolderEditor from "../components/FolderEditor";
import { EmptyState, PageHeader, errText } from "../components/ui";
import * as ipc from "../lib/ipc";
import { confirmDestructive } from "../lib/dialog";
import { fmtSize } from "../lib/format";
import type { FolderSetting } from "../lib/types";
import { useAppStore } from "../stores/app";
import { toast } from "../stores/toast";

const NEW_FOLDER: FolderSetting = {
  name: "",
  inputPath: "",
  outputPath: "",
  preset: "",
  fileExtensions: [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".m2ts"],
  deleteSource: false,
  recursive: true,
  useStaging: true,
  stagingPath: "",
  minFileSizeBytes: 0,
  maxFileSizeBytes: 0,
  resolutionPresets: [],
};

export default function FoldersPage() {
  const settings = useAppStore((s) => s.settings);
  const saveSettings = useAppStore((s) => s.saveSettings);
  const [editing, setEditing] = useState<{ index: number | null; folder: FolderSetting } | null>(null);

  if (!settings) return null;
  const folders = settings.folders;

  const persist = async (next: FolderSetting[], what: string) => {
    try {
      await saveSettings({ ...settings, folders: next });
      toast.success(what);
    } catch (e) {
      toast.error(errText(e));
    }
  };

  const save = (f: FolderSetting) => {
    const next = editing?.index == null ? [...folders, f] : folders.map((x, i) => (i === editing.index ? f : x));
    setEditing(null);
    void persist(next, editing?.index == null ? "Folder added. It's scanned at the next interval, or press Scan now." : "Folder updated.");
  };

  const remove = async (i: number) => {
    const ok = await confirmDestructive(`Stop watching ${folders[i].name || folders[i].inputPath}? Queued jobs from it keep going.`, "Remove folder");
    if (ok) void persist(folders.filter((_, j) => j !== i), "Folder removed.");
  };

  const openPath = (p: string) => ipc.openFolder(p).catch((e) => toast.error(errText(e)));

  return (
    <div className="flex min-h-full flex-col">
      <PageHeader title="Watched folders" subtitle="Each folder has its own destination and preset">
        <button className="btn-primary" onClick={() => setEditing({ index: null, folder: { ...NEW_FOLDER, preset: "" } })}>
          <Plus size={14} />
          Add folder
        </button>
      </PageHeader>

      {folders.length === 0 ? (
        <EmptyState icon={<FolderCog size={36} />} title="No folders are watched yet">
          Add one: video files that appear in it are encoded into the destination you choose. Uploads from AutoMk use the first folder whose name or path mentions
          “Movies” or “TV”.
        </EmptyState>
      ) : (
        <div className="grid gap-4 px-6 py-5 xl:grid-cols-2">
          {folders.map((f, i) => {
            const problems: string[] = [];
            if (!f.inputPath) problems.push("no input folder");
            if (!f.outputPath) problems.push("no output folder");
            return (
              <section key={i} className="card p-4">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="truncate text-base font-semibold text-content">{f.name || f.inputPath}</div>
                    <div className="mt-0.5 text-xs text-content-muted">
                      {f.preset || settings.defaultPreset}
                      {f.resolutionPresets.length > 0 && ` · ${f.resolutionPresets.length} resolution rule${f.resolutionPresets.length === 1 ? "" : "s"}`}
                    </div>
                  </div>
                  <div className="flex shrink-0 gap-1">
                    <button className="btn-chip" onClick={() => setEditing({ index: i, folder: f })}>
                      <Pencil size={12} />
                      Edit
                    </button>
                    <button className="btn-chip hover:!border-critical hover:!text-critical" onClick={() => void remove(i)} title="Stop watching">
                      <Trash2 size={12} />
                    </button>
                  </div>
                </div>
                <div className="mono mt-3 flex flex-col gap-1 text-content-secondary">
                  <button className="flex min-w-0 items-center gap-2 text-left hover:text-content" onClick={() => void openPath(f.inputPath)} title="Open">
                    <FolderOpen size={12} className="shrink-0 text-content-muted" />
                    <span className="truncate">{f.inputPath || "—"}</span>
                  </button>
                  <button className="flex min-w-0 items-center gap-2 text-left hover:text-content" onClick={() => void openPath(f.outputPath)} title="Open">
                    <ArrowRight size={12} className="shrink-0 text-content-muted" />
                    <span className="truncate">{f.outputPath || "—"}</span>
                  </button>
                </div>
                <div className="mt-3 flex flex-wrap gap-1.5 text-[11px]">
                  <span className="tint-muted rounded px-1.5 py-0.5">{f.fileExtensions.join(" ")}</span>
                  {f.recursive && <span className="tint-muted rounded px-1.5 py-0.5">subfolders</span>}
                  {f.useStaging && <span className="tint-info rounded px-1.5 py-0.5">staged{f.stagingPath ? ` in ${f.stagingPath}` : ""}</span>}
                  {f.deleteSource && <span className="tint-warning rounded px-1.5 py-0.5">deletes source</span>}
                  {(f.minFileSizeBytes > 0 || f.maxFileSizeBytes > 0) && (
                    <span className="tint-muted rounded px-1.5 py-0.5">
                      {f.minFileSizeBytes > 0 ? `≥ ${fmtSize(f.minFileSizeBytes)}` : ""} {f.maxFileSizeBytes > 0 ? `≤ ${fmtSize(f.maxFileSizeBytes)}` : ""}
                    </span>
                  )}
                  {problems.map((p) => (
                    <span key={p} className="tint-critical rounded px-1.5 py-0.5">
                      {p}
                    </span>
                  ))}
                </div>
              </section>
            );
          })}
        </div>
      )}

      {editing && <FolderEditor folder={editing.folder} isNew={editing.index == null} onSave={save} onClose={() => setEditing(null)} />}
    </div>
  );
}
