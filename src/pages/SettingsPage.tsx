import { useEffect, useState } from "react";
import { CheckCircle2, Save } from "lucide-react";
import PathInput from "../components/PathInput";
import PresetInput from "../components/PresetInput";
import { Field, PageHeader, Section, Spinner, Toggle, errText } from "../components/ui";
import { isWindows } from "../components/WindowControls";
import * as ipc from "../lib/ipc";
import { confirmDestructive } from "../lib/dialog";
import { bytesToGb, gbToBytes } from "../lib/format";
import type { Settings } from "../lib/types";
import { useAppStore } from "../stores/app";
import { useNavStore } from "../stores/nav";
import { toast } from "../stores/toast";

export default function SettingsPage() {
  const saved = useAppStore((s) => s.settings);
  const status = useAppStore((s) => s.status);
  const saveSettings = useAppStore((s) => s.saveSettings);
  const setLeaveGuard = useNavStore((s) => s.setLeaveGuard);
  const [s, setS] = useState<Settings | null>(saved);
  const [busy, setBusy] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; text: string } | null>(null);
  const [testing, setTesting] = useState(false);

  useEffect(() => {
    if (!s && saved) setS(saved);
  }, [saved, s]);

  const dirty = !!s && !!saved && JSON.stringify(s) !== JSON.stringify(saved);
  useEffect(() => {
    setLeaveGuard(dirty ? () => confirmDestructive("Leave without saving your settings?", "Unsaved settings") : null);
    return () => setLeaveGuard(null);
  }, [dirty, setLeaveGuard]);

  if (!s) return null;
  const set = <K extends keyof Settings>(k: K, v: Settings[K]) => setS({ ...s, [k]: v });

  const save = async () => {
    setBusy(true);
    try {
      setS(await saveSettings(s));
      toast.success("Settings saved.");
    } catch (e) {
      toast.error(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const runTest = async () => {
    setTesting(true);
    setTest(null);
    try {
      setTest({ ok: true, text: await ipc.testHandbrake(s.handbrakePath) });
    } catch (e) {
      setTest({ ok: false, text: errText(e) });
    } finally {
      setTesting(false);
    }
  };

  const windowOn = !!(s.startTime || s.endTime);

  return (
    <div className="flex min-h-full flex-col">
      <PageHeader title="Settings" subtitle="Saved on this PC. Watched folders have their own page.">
        <button className="btn-primary" onClick={() => void save()} disabled={!dirty || busy}>
          {busy ? <Spinner size={14} /> : <Save size={14} />}
          Save
        </button>
      </PageHeader>
      <div className="grid max-w-5xl gap-4 px-6 py-5 xl:grid-cols-2">
        <Section title="HandBrake">
          <Field label="HandBrakeCLI" hint={isWindows ? "Usually C:\\Program Files\\HandBrake\\HandBrakeCLI.exe (the CLI is a separate download from the GUI)." : "Homebrew puts it at /opt/homebrew/bin/HandBrakeCLI (or /usr/local/bin). Use the full path: apps started from Finder don't see the shell's PATH."}>
            <PathInput value={s.handbrakePath} onChange={(v) => set("handbrakePath", v)} />
          </Field>
          <div className="flex items-center gap-3">
            <button className="btn-ghost" onClick={() => void runTest()} disabled={testing}>
              {testing ? <Spinner size={14} /> : <CheckCircle2 size={14} />}
              Test
            </button>
            {test ? (
              <span className={`min-w-0 break-words text-xs ${test.ok ? "text-good" : "text-critical"}`}>{test.text}</span>
            ) : (
              status?.handbrakeVersion && <span className="text-xs text-content-muted">{status.handbrakeVersion}</span>
            )}
          </div>
          <Field label="Default preset" hint="Used by folders that don't set their own.">
            <PresetInput value={s.defaultPreset} onChange={(v) => set("defaultPreset", v)} />
          </Field>
          <Field label="Staging folder" hint="For folders that stage but don't name their own folder. Empty = the system temp folder. Needs room for a source and its encode.">
            <PathInput directory value={s.stagingDir} onChange={(v) => set("stagingDir", v)} placeholder="System temp folder" />
          </Field>
          <Field label="Tries per file" hint="A failed encode is queued again until it has had this many tries.">
            <input className="field w-20 tabular-nums" inputMode="numeric" value={s.maxAttempts} onChange={(e) => set("maxAttempts", Number(e.target.value.replace(/\D/g, "")) || 1)} />
          </Field>
          <Toggle checked={s.keepJobLogs} onChange={(v) => set("keepJobLogs", v)} label="Keep HandBrake's full output per job" hint="Written to the logs folder; handy when an encode fails." />
        </Section>

        <Section title="Scanning">
          <Toggle checked={s.monitoringEnabled} onChange={(v) => set("monitoringEnabled", v)} label="Keep watching the folders" hint="Off: folders are scanned once at startup and when you press Scan now." />
          <Field label="Scan every (minutes)">
            <input
              className="field w-24 tabular-nums"
              inputMode="numeric"
              value={s.scanIntervalMinutes}
              onChange={(e) => set("scanIntervalMinutes", Number(e.target.value.replace(/\D/g, "")) || 1)}
              disabled={!s.monitoringEnabled}
            />
          </Field>
          <Toggle
            checked={windowOn}
            onChange={(v) => setS({ ...s, startTime: v ? "22:00" : "", endTime: v ? "06:00" : "" })}
            label="Only encode during a time window"
            hint="Files are still found and queued at any time; encodes wait for the window. A window that ends before it starts spans midnight."
          />
          {windowOn && (
            <div className="flex items-end gap-3">
              <Field label="From">
                <input className="field w-28" type="time" value={s.startTime.slice(0, 5)} onChange={(e) => set("startTime", e.target.value)} />
              </Field>
              <Field label="To">
                <input className="field w-28" type="time" value={s.endTime.slice(0, 5)} onChange={(e) => set("endTime", e.target.value)} />
              </Field>
            </div>
          )}
        </Section>

        <Section title="Uploads from AutoMk">
          <Toggle
            checked={s.serverEnabled}
            onChange={(v) => set("serverEnabled", v)}
            label="Accept uploads over HTTP"
            hint="AutoMk sends finished rips to /upload. They land in the incoming folder and are queued with the settings of the folder that matches (Movies / TV)."
          />
          <Field label="Listen on" hint="host:port. 0.0.0.0 = every network interface; 127.0.0.1 = this PC only. Windows may ask to allow it through the firewall; on a Mac, port 5000 is taken by AirPlay, so the default there is 5050 (set the same port in AutoMk).">
            <input className="field mono w-full" value={s.listenAddress} onChange={(e) => set("listenAddress", e.target.value)} placeholder="0.0.0.0:5000" spellCheck={false} disabled={!s.serverEnabled} />
          </Field>
          {status?.server.error && <span className="text-xs text-critical">{status.server.error}</span>}
          <Field label="Incoming folder">
            <PathInput directory value={s.incomingDir} onChange={(v) => set("incomingDir", v)} />
          </Field>
          <Field label="Largest upload (GB)">
            <input className="field w-28 tabular-nums" value={bytesToGb(s.maxUploadBytes)} onChange={(e) => set("maxUploadBytes", gbToBytes(e.target.value) || 100 * 1024 ** 3)} />
          </Field>
        </Section>
      </div>
    </div>
  );
}
