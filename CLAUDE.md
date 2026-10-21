# CLAUDE.md

**HandbrakeScheduler 2**: watch folders (and take uploads from AutoMk), encode videos with HandBrakeCLI inside a time window. Tauri 2 + Rust backend, React + TypeScript frontend. UI, styling and conventions follow `../auto-mkv` (which follows `../portside-lite`).

## The old project

This replaces the .NET 10 console service in `_legacy/src/` (to be deleted once v2 has run for a while). Look there for prior behavior before reinventing it:
- `HandBrakeSettings.cs`: the `appsettings.json` shape (ported to `hbs-core/settings.rs`, same fields, camelCase; `Username`/`Password` network credentials were never implemented and are dropped).
- `HandBrakeCli.cs`: HandBrake invocation, the progress regex, the 3-minute no-output timeout (ported to `hbs-engine/runner.rs` + `hbs-core/handbrake.rs`).
- `HandBrakeService.cs` / `TempFileManager.cs`: `_moved` parking, staging copy, pre-flight writable check (ported to `engine.rs::process` + `files.rs`).
- `Program.cs` (`FileReceiver`) / `FileReceiverService.cs`: the upload contract and the Movie/TV routing of uploads (ported to `server.rs`, `hbs-core/transfer.rs`, `routing.rs`). **AutoMk's `automk-rip/transfer.rs` speaks this contract; keep them identical.**
- `VideoInfoService.cs`: `--scan` parsing for resolution presets (`handbrake.rs::parse_scan`).
- Not ported: the Spectre.Console dashboard (replaced by the app), `appsettings.json` / env-var config (settings live in the app data folder), running as a Windows service.

Differences from v1 worth knowing: files already queued or done aren't re-queued by later scans (v1 re-queued every scan); failed jobs really are retried (v1 only documented it); `DeleteSource` now actually deletes the parked source after success (v1 never deleted); uploads are stored under the relative path AutoMk sends, not just the file name.

## Build & verify

> **Run dev/prod builds from Windows, not WSL.** `npm run tauri dev` / `tauri build` need the MSVC toolchain + Windows Node. WSL is for editing and checks only.

**In WSL (checks only):**
```bash
export CARGO_TARGET_DIR=$HOME/.cargo-target/handbrake-scheduler
cargo test -p hbs-core -p hbs-engine      # unit tests + tests/flow.rs (fake HandBrakeCLI shell script, unix only)
scripts/check-tauri-wsl.sh                # type-checks src-tauri using stub pkg-config files
npx tsc --noEmit
npx vite build
```

- `node_modules` was first installed from WSL: run `npm install` again on Windows before building (otherwise rollup/tailwind have Linux native binaries).
- Vite's file watcher doesn't see edits on `/mnt/f` from WSL. Restart the dev server. Browser preview (`npm run dev`, no Tauri) runs on `src/dev/mockTauri.ts`.
- No outgoing HTTP, so no TLS stack. The server is axum (http1 only).
- Testing without HandBrake: `crates/hbs-engine/tests/flow.rs` has a fake `HandBrakeCLI` shell script; point Settings at a similar script to try the app.

## Architecture

```
crates/hbs-core     pure, unit-tested: settings.rs (Settings/FolderSetting/ResolutionPreset, preset_for),
                    handbrake.rs (progress / --scan / --preset-list parsing), schedule.rs (time window),
                    job.rs (Job DTO, recover/prune), paths.rs (output names, _moved, safe relative paths),
                    routing.rs (upload → folder), transfer.rs (the AutoMk upload contract, PascalCase).
crates/hbs-engine   IO: runner.rs (HandBrakeCLI process, \r-split lines, stall kill, Windows job object),
                    files.rs (copy with progress, park_in_moved, free space, writable probe),
                    scanner.rs (walk a watched folder), server.rs (axum: /upload /files /jobs /stop /health),
                    engine.rs (queue, scan loop, process loop, status, log, settings).
src-tauri           thin: commands.rs wraps the engine; lib.rs forwards EventSink to webview events.
src                 React: pages/ (Queue, Folders, Settings), components/ (FolderEditor drawer, PresetInput…), stores/ (Zustand).
```

**Layering rule:** only `src-tauri` imports Tauri. Put testable logic in `hbs-core`.

## Key conventions

- **Flow.** `scan_loop` (every `scan_interval_minutes`, or "Scan now") walks each folder → skips `_moved`, `.part`, files changed < 15 s ago, and anything that already has an open or completed job → probes with `--scan` (resolution → preset) → `enqueue`. `process_loop` takes the first pending job when not paused and the window is open: park the source in `_moved` → (staging: copy to the staging folder if 1.5× the size is free) → `HandBrakeCLI --input … --output <dir>/<stem>.mp4 --preset … --verbose=1` → (staging: copy out) → delete the source if the folder says so. One encode at a time.
- **Uploads** (`server.rs`) stream to `<incoming>/upload-*.part`, rename to `<incoming>/<RelativeFilePath>` (or the file name), answer 200 at once, then `queue_upload` routes to a folder by "movie"/"tv"/"series" in the relative path (else the first folder), probes, and queues with `delete_source = true` and no staging. If there's no folder at all the file stays in incoming and the log says so.
- **Retries.** A job error is `Retry` (HandBrake failed, copy failed, stalled) or `Fatal` (source gone). Retry puts the job back at the end of the queue until `max_attempts`. Pausing cancels the running encode and requeues it without counting the try. Closing the app with an encode running asks first; `quit_app` stops it and it's picked up next launch (`recover()`).
- **Status** (`EngineStatus`) is derived in `refresh_status()` from: running job, paused, scanning, pending count, window. Call it after every change that affects those; it also carries `problems` from `Settings::problems()` for the status bar.
- **Jobs persist** as `jobs.json` in the app data folder (atomic write), finished ones capped at 200. Settings are `settings.json` there too. Logs go to `logs/handbrake-scheduler.log`, and per-job HandBrake output to `logs/jobs/<id>.log` when `keep_job_logs` is on.
- **HandBrake output** arrives with `\r` between progress updates: `runner::LineSplitter` splits on `\r` and `\n`. `--scan` output is on stderr; `capture()` merges both streams.
- **DTOs are camelCase** (serde), mirrored by hand in `src/lib/types.ts`; IPC wrappers in `src/lib/ipc.ts`. Events: `jobs:changed` (whole list), `job:progress` (one job, throttled to 300 ms), `status:changed`, `log:line`, `app:close-requested`. The upload contract (`transfer.rs`, `/files`) stays PascalCase.
- **Theming:** Portside's tokens in `src/index.css` (dark default). Use the semantic utilities and component classes (`btn-*`, `field`, `card`, `table`, `tint-*`). Status colors are for state only, always with an icon or label.
- **Zustand selectors must return existing references** (select raw fields, derive with `useMemo`).
- **Dialogs:** `confirmDestructive` (`src/lib/dialog.ts`), never `window.confirm`. Folder editing happens in a draft inside `FolderEditor`; the Folders page saves the whole settings object.
