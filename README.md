# HandbrakeScheduler

Watch folders for video files and encode them with HandBrakeCLI, on a schedule, from a desktop app (Tauri 2, Windows first).

1. Add a **watched folder**: where to look, where encodes go, which HandBrake preset to use. Optional rules pick another preset by source resolution (4K → HEVC, for instance).
2. Files that show up are queued at the next scan and encoded one at a time to `Name.mp4`. Sources are parked in a `_moved` folder next to where they were found (and deleted afterwards if the folder says so).
3. Set a **time window** (say 22:00 to 06:00) and encodes wait for it; files are still found and queued at any time.
4. Turn on **uploads** and [AutoMk](https://github.com/timothydodd/auto-mkv) can send finished rips straight in (`POST /upload`). They're queued with the settings of the folder whose name or path mentions Movies or TV.

Folders on a NAS? Tick **staging** on the folder: the source is copied locally, encoded there, and the encode is copied out.

The queue survives a restart. A failed encode is tried again (three times by default), and the Queue page shows why it failed.

HandbrakeScheduler 2 replaces the .NET console service (kept in `_legacy/` for reference for now).

## Needs

- [HandBrakeCLI](https://handbrake.fr/downloads2.php) (a separate download from the HandBrake GUI)

## Develop

```bash
npm install
npm run tauri dev
```

See `CLAUDE.md` for the architecture and checks.
