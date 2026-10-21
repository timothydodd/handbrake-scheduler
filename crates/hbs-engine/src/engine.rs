//! The engine: the job queue, folder scans, the processing loop (one encode
//! at a time, only inside the time window) and the upload server's lifetime.
//!
//! Locks are `std::sync::Mutex` and are never held across an `.await`.

use std::collections::VecDeque;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::time::{Duration, Instant};

use chrono::Timelike;
use hbs_core::handbrake::{is_error_line, parse_preset_list, parse_progress, parse_scan, VideoInfo};
use hbs_core::job::{prune, recover};
use hbs_core::paths::{is_in_moved, output_dir_for, output_name};
use hbs_core::routing::{route_upload, upload_output_dir};
use hbs_core::schedule::Clock;
use hbs_core::transfer::ReceivedFile;
use hbs_core::{now_ms, FolderSetting, Job, JobPhase, JobProgress, JobSource, JobStatus, Settings};
use serde::Serialize;
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

use crate::files::{copy_file, free_space, is_writable_dir, park_in_moved};
use crate::runner::{capture, run_handbrake, Outcome};
use crate::scanner::find_files;
use crate::server::{self, ServerHandle};
use crate::{Error, EventSink, Result};

/// An encode with no output for this long is killed and retried.
const ENCODE_STALL: Duration = Duration::from_secs(3 * 60);
const PROBE_TIMEOUT: Duration = Duration::from_secs(30);
/// A file changed more recently than this may still be being written.
const MIN_FILE_AGE: Duration = Duration::from_secs(15);
const EMIT_EVERY: Duration = Duration::from_millis(300);
const LOG_KEEP: usize = 800;
const JOBS_KEEP_FINISHED: usize = 200;
const RECEIVED_KEEP: usize = 200;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LogLine {
    pub ts_ms: i64,
    /// "queue", "scan", "server", "handbrake", or a file name.
    pub source: String,
    pub text: String,
    pub error: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum EngineState {
    #[default]
    Idle,
    Scanning,
    Encoding,
    /// Jobs are queued but the time window is closed.
    WaitingForWindow,
    Paused,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerStatus {
    pub running: bool,
    pub address: String,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineStatus {
    pub state: EngineState,
    pub paused: bool,
    pub current_job: Option<String>,
    pub next_scan_ms: Option<i64>,
    /// When the time window next opens, while waiting for it.
    pub window_opens_ms: Option<i64>,
    /// "22:00–06:00" when a window is set.
    pub window: Option<String>,
    pub pending: usize,
    pub server: ServerStatus,
    pub handbrake_version: Option<String>,
    pub problems: Vec<String>,
}

/// Why a job stopped.
enum JobError {
    /// Worth another try (HandBrake failed, copy failed).
    Retry(String),
    /// Trying again can't help (the source is gone).
    Fatal(String),
}

impl From<Error> for JobError {
    fn from(e: Error) -> Self {
        JobError::Retry(e.to_string())
    }
}

/// Files made while a job ran, to clean up afterwards.
#[derive(Default)]
struct Work {
    /// Copy of the source in the staging folder.
    temp_input: Option<PathBuf>,
    /// The encode, wherever HandBrake wrote it (deleted on failure).
    encode: Option<PathBuf>,
    /// The folder HandBrake wrote to.
    encode_dir: Option<PathBuf>,
}

pub struct Engine {
    settings: RwLock<Settings>,
    settings_path: PathBuf,
    jobs_path: PathBuf,
    logs_dir: PathBuf,
    videos_dir: PathBuf,
    sink: Arc<dyn EventSink>,
    jobs: Mutex<Vec<Job>>,
    received: Mutex<Vec<ReceivedFile>>,
    status: Mutex<EngineStatus>,
    /// The running job and the token that stops it.
    current: Mutex<Option<(String, CancellationToken)>>,
    paused: AtomicBool,
    scanning: AtomicBool,
    scan_requested: AtomicBool,
    wake_scan: Notify,
    wake_process: Notify,
    server: tokio::sync::Mutex<Option<ServerHandle>>,
    log: Mutex<VecDeque<LogLine>>,
    log_file: Mutex<Option<std::fs::File>>,
    last_progress_emit: Mutex<Instant>,
    next_id: AtomicU64,
}

fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, serde_json::to_vec_pretty(value)?)?;
    std::fs::rename(&tmp, path)?;
    Ok(())
}

fn now_clock() -> Clock {
    let t = chrono::Local::now();
    Clock::hms(t.hour(), t.minute(), t.second())
}

fn same_file(a: &str, b: &str) -> bool {
    let norm = |s: &str| s.replace('\\', "/").to_ascii_lowercase();
    norm(a) == norm(b)
}

impl Engine {
    pub fn new(data_dir: PathBuf, videos_dir: PathBuf, sink: Arc<dyn EventSink>) -> Arc<Self> {
        let settings_path = data_dir.join("settings.json");
        let jobs_path = data_dir.join("jobs.json");
        let logs_dir = data_dir.join("logs");
        let mut settings: Settings = std::fs::read(&settings_path)
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default();
        settings.normalize(&videos_dir);
        let mut jobs: Vec<Job> = std::fs::read(&jobs_path)
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default();
        recover(&mut jobs);
        let _ = std::fs::create_dir_all(&logs_dir);
        let log_file = std::fs::OpenOptions::new().create(true).append(true).open(logs_dir.join("handbrake-scheduler.log")).ok();
        let engine = Arc::new(Self {
            status: Mutex::new(EngineStatus { paused: false, problems: settings.problems(), ..Default::default() }),
            settings: RwLock::new(settings),
            settings_path,
            jobs_path,
            logs_dir,
            videos_dir,
            sink,
            jobs: Mutex::new(jobs),
            received: Mutex::new(Vec::new()),
            current: Mutex::new(None),
            paused: AtomicBool::new(false),
            scanning: AtomicBool::new(false),
            scan_requested: AtomicBool::new(false),
            wake_scan: Notify::new(),
            wake_process: Notify::new(),
            server: tokio::sync::Mutex::new(None),
            log: Mutex::new(VecDeque::new()),
            log_file: Mutex::new(log_file),
            last_progress_emit: Mutex::new(Instant::now()),
            next_id: AtomicU64::new(1),
        });
        engine.log("queue", &format!("Loaded {} job(s) from last time.", engine.jobs.lock().expect("jobs lock").len()), false);
        engine
    }

    // --- settings ------------------------------------------------------------

    pub fn settings(&self) -> Settings {
        self.settings.read().expect("settings lock").clone()
    }

    pub async fn save_settings(self: &Arc<Self>, mut s: Settings) -> Result<Settings> {
        s.normalize(&self.videos_dir);
        write_json_atomic(&self.settings_path, &s)?;
        let old = std::mem::replace(&mut *self.settings.write().expect("settings lock"), s.clone());
        let server_changed = old.server_enabled != s.server_enabled
            || old.listen_address != s.listen_address
            || old.max_upload_bytes != s.max_upload_bytes;
        if server_changed {
            self.restart_server().await;
        }
        if old.handbrake_path != s.handbrake_path {
            let me = Arc::clone(self);
            tokio::spawn(async move { me.check_handbrake().await });
        }
        self.refresh_status();
        self.wake_scan.notify_one();
        self.wake_process.notify_one();
        Ok(s)
    }

    // --- log -----------------------------------------------------------------

    pub fn log(&self, source: &str, text: &str, error: bool) {
        let line = LogLine { ts_ms: now_ms(), source: source.to_string(), text: text.to_string(), error };
        {
            let mut log = self.log.lock().expect("log lock");
            log.push_back(line.clone());
            while log.len() > LOG_KEEP {
                log.pop_front();
            }
        }
        if let Some(f) = self.log_file.lock().expect("log file lock").as_mut() {
            let stamp = chrono::Local::now().format("%Y-%m-%d %H:%M:%S");
            let _ = writeln!(f, "{stamp} {} [{source}] {text}", if error { "ERR " } else { "INFO" });
        }
        self.sink.log(&line);
    }

    pub fn log_lines(&self) -> Vec<LogLine> {
        self.log.lock().expect("log lock").iter().cloned().collect()
    }

    // --- status --------------------------------------------------------------

    pub fn status(&self) -> EngineStatus {
        self.status.lock().expect("status lock").clone()
    }

    /// Recompute the derived parts of the status and tell the UI.
    pub fn refresh_status(&self) {
        let settings = self.settings();
        let pending = self.jobs.lock().expect("jobs lock").iter().filter(|j| j.status == JobStatus::Pending).count();
        let current = self.current.lock().expect("current lock").as_ref().map(|(id, _)| id.clone());
        let paused = self.paused.load(Ordering::SeqCst);
        let window = settings.window();
        let now = now_clock();
        let waiting = window.filter(|w| !w.contains(now)).map(|w| now_ms() + i64::from(w.secs_until_open(now)) * 1000);
        let state = if current.is_some() {
            EngineState::Encoding
        } else if paused {
            EngineState::Paused
        } else if self.scanning.load(Ordering::SeqCst) {
            EngineState::Scanning
        } else if pending > 0 && waiting.is_some() {
            EngineState::WaitingForWindow
        } else {
            EngineState::Idle
        };
        let snapshot = {
            let mut st = self.status.lock().expect("status lock");
            st.state = state;
            st.paused = paused;
            st.current_job = current;
            st.pending = pending;
            st.window = window.map(|w| format!("{}–{}", w.start, w.end));
            st.window_opens_ms = if pending > 0 { waiting } else { None };
            st.problems = settings.problems();
            st.clone()
        };
        self.sink.status_changed(&snapshot);
    }

    fn set_status(&self, f: impl FnOnce(&mut EngineStatus)) {
        f(&mut self.status.lock().expect("status lock"));
        self.refresh_status();
    }

    // --- jobs ----------------------------------------------------------------

    pub fn jobs(&self) -> Vec<Job> {
        self.jobs.lock().expect("jobs lock").clone()
    }

    pub fn received(&self) -> Vec<ReceivedFile> {
        self.received.lock().expect("received lock").clone()
    }

    fn save_jobs(&self) {
        let snapshot = {
            let mut jobs = self.jobs.lock().expect("jobs lock");
            prune(&mut jobs, JOBS_KEEP_FINISHED);
            jobs.clone()
        };
        if let Err(e) = write_json_atomic(&self.jobs_path, &snapshot) {
            self.log("queue", &format!("Couldn't save the queue: {e}"), true);
        }
    }

    fn emit_jobs(&self) {
        let jobs = self.jobs();
        self.sink.jobs_changed(&jobs);
    }

    /// Save, tell the UI, recompute the status.
    fn jobs_updated(&self) {
        self.save_jobs();
        self.emit_jobs();
        self.refresh_status();
    }

    fn update_job(&self, id: &str, f: impl FnOnce(&mut Job)) -> Option<Job> {
        let mut jobs = self.jobs.lock().expect("jobs lock");
        let job = jobs.iter_mut().find(|j| j.id == id)?;
        f(job);
        Some(job.clone())
    }

    fn new_id(&self) -> String {
        format!("j{}-{}", now_ms(), self.next_id.fetch_add(1, Ordering::SeqCst))
    }

    /// Whether a file already has a job that would make a second one a duplicate.
    fn has_job_for(jobs: &[Job], path: &str) -> bool {
        jobs.iter().any(|j| (j.is_open() || j.status == JobStatus::Completed) && (same_file(&j.original_path, path) || same_file(&j.input_path, path)))
    }

    /// Add a job unless the file is already queued or done. Returns the job when added.
    fn enqueue(&self, mut job: Job) -> Option<Job> {
        job.id = self.new_id();
        job.created_ms = now_ms();
        job.status = JobStatus::Pending;
        {
            let mut jobs = self.jobs.lock().expect("jobs lock");
            if Self::has_job_for(&jobs, &job.original_path) {
                return None;
            }
            jobs.push(job.clone());
        }
        self.log(
            "queue",
            &format!("Queued {} ({:.1} MB) → {} with preset \"{}\"", job.file_name(), job.size_bytes as f64 / 1048576.0, job.output_dir, job.preset),
            false,
        );
        self.jobs_updated();
        self.wake_process.notify_one();
        Some(job)
    }

    /// Queue files picked by hand. They're processed with the settings of the
    /// watched folder they're in (or the one given), else the first folder.
    pub async fn add_files(self: &Arc<Self>, paths: Vec<String>, folder_index: Option<usize>) -> Result<Vec<Job>> {
        let settings = self.settings();
        if settings.folders.is_empty() {
            return Err(Error::Msg("Add a watched folder first: its output folder and preset are used for the files.".into()));
        }
        let mut added = Vec::new();
        let mut skipped = 0;
        for p in paths {
            let path = PathBuf::from(&p);
            let Ok(meta) = std::fs::metadata(&path) else {
                return Err(Error::Msg(format!("{p} isn't there.")));
            };
            if !meta.is_file() {
                continue;
            }
            let folder = folder_index
                .and_then(|i| settings.folders.get(i))
                .or_else(|| settings.folders.iter().find(|f| path.starts_with(&f.input_path)))
                .or_else(|| settings.folders.first())
                .expect("a folder");
            let output_dir = if path.starts_with(&folder.input_path) {
                output_dir_for(Path::new(&folder.input_path), &path, Path::new(&folder.output_path))
            } else {
                PathBuf::from(&folder.output_path)
            };
            let info = self.probe(&settings.handbrake_path, &path).await;
            let job = self.job_for(folder, &path, meta.len(), &info, output_dir, JobSource::Manual);
            match self.enqueue(job) {
                Some(j) => added.push(j),
                None => skipped += 1,
            }
        }
        if added.is_empty() && skipped > 0 {
            return Err(Error::Msg("Those files are already queued.".into()));
        }
        Ok(added)
    }

    fn job_for(&self, folder: &FolderSetting, path: &Path, size: u64, info: &VideoInfo, output_dir: PathBuf, source: JobSource) -> Job {
        let settings = self.settings();
        let staging_dir = if folder.use_staging {
            if !folder.staging_path.trim().is_empty() {
                folder.staging_path.trim().to_string()
            } else if !settings.staging_dir.trim().is_empty() {
                settings.staging_dir.trim().to_string()
            } else {
                std::env::temp_dir().join("HandbrakeScheduler").to_string_lossy().to_string()
            }
        } else {
            String::new()
        };
        Job {
            source,
            input_path: path.to_string_lossy().to_string(),
            original_path: path.to_string_lossy().to_string(),
            folder_name: folder.name.clone(),
            output_dir: output_dir.to_string_lossy().to_string(),
            preset: folder.preset_for(info.width, info.height),
            delete_source: folder.delete_source,
            staging_dir,
            size_bytes: size,
            width: info.width,
            height: info.height,
            ..Default::default()
        }
    }

    /// A file AutoMk sent: route it to a folder's settings and queue it.
    /// Returns at once; the probe and the queueing happen in the background.
    pub fn queue_upload(self: &Arc<Self>, received: ReceivedFile) {
        {
            let mut list = self.received.lock().expect("received lock");
            list.push(received.clone());
            while list.len() > RECEIVED_KEEP {
                list.remove(0);
            }
        }
        self.log("server", &format!("Received {} ({:.1} MB)", received.original_file_name, received.file_size_bytes as f64 / 1048576.0), false);
        let me = Arc::clone(self);
        tokio::spawn(async move {
            let settings = me.settings();
            let Some(folder) = route_upload(&settings.folders, &received.relative_file_path).cloned() else {
                me.log("server", &format!("No watched folder to take {}: add one in Settings (it's kept in the incoming folder).", received.original_file_name), true);
                return;
            };
            let path = PathBuf::from(&received.file_path);
            let info = me.probe(&settings.handbrake_path, &path).await;
            let mut job = me.job_for(&folder, &path, received.file_size_bytes, &info, upload_output_dir(&folder, &received.relative_file_path), JobSource::Upload);
            job.folder_name = format!("Uploads → {}", folder.name);
            // Uploads are local already and go once the encode is out.
            job.staging_dir.clear();
            job.delete_source = true;
            me.enqueue(job);
        });
    }

    pub fn cancel_job(&self, id: &str) -> Result<()> {
        let running = self.current.lock().expect("current lock").as_ref().filter(|(cur, _)| cur == id).map(|(_, t)| t.clone());
        if let Some(token) = running {
            self.log("queue", "Cancelling the running encode.", false);
            token.cancel();
            return Ok(());
        }
        let job = self
            .update_job(id, |j| {
                if j.status == JobStatus::Pending {
                    j.status = JobStatus::Cancelled;
                    j.finished_ms = Some(now_ms());
                }
            })
            .ok_or_else(|| Error::Msg("That job is gone.".into()))?;
        self.log("queue", &format!("Cancelled {}.", job.file_name()), false);
        self.jobs_updated();
        Ok(())
    }

    /// Put a finished job back in the queue.
    pub fn retry_job(&self, id: &str) -> Result<()> {
        let job = {
            let mut jobs = self.jobs.lock().expect("jobs lock");
            let pos = jobs.iter().position(|j| j.id == id).ok_or_else(|| Error::Msg("That job is gone.".into()))?;
            if jobs[pos].is_open() {
                return Err(Error::Msg("That job is still queued.".into()));
            }
            if !Path::new(&jobs[pos].input_path).is_file() {
                return Err(Error::Msg(format!("{} isn't there any more.", jobs[pos].input_path)));
            }
            let mut job = jobs.remove(pos);
            job.status = JobStatus::Pending;
            job.attempts = 0;
            job.error = None;
            job.started_ms = None;
            job.finished_ms = None;
            job.output_file = None;
            job.progress = JobProgress::default();
            jobs.push(job.clone());
            job
        };
        self.log("queue", &format!("Queued {} again.", job.file_name()), false);
        self.jobs_updated();
        self.wake_process.notify_one();
        Ok(())
    }

    pub fn remove_job(&self, id: &str) -> Result<()> {
        {
            let mut jobs = self.jobs.lock().expect("jobs lock");
            let pos = jobs.iter().position(|j| j.id == id).ok_or_else(|| Error::Msg("That job is gone.".into()))?;
            if jobs[pos].is_open() {
                return Err(Error::Msg("Cancel it first.".into()));
            }
            jobs.remove(pos);
        }
        self.jobs_updated();
        Ok(())
    }

    pub fn clear_finished(&self) {
        self.jobs.lock().expect("jobs lock").retain(|j| j.is_open());
        self.jobs_updated();
    }

    /// Pausing stops the running encode and puts it back in the queue.
    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::SeqCst);
        if paused {
            if let Some((_, token)) = self.current.lock().expect("current lock").as_ref() {
                token.cancel();
            }
            self.log("queue", "Paused.", false);
        } else {
            self.log("queue", "Resumed.", false);
        }
        self.refresh_status();
        self.wake_process.notify_one();
    }

    /// Kill the running encode (it's retried like any failure).
    pub fn stop_current(&self, by: &str) {
        if let Some((id, token)) = self.current.lock().expect("current lock").as_ref() {
            self.log("queue", &format!("Stopping {id}, asked by {by}."), false);
            token.cancel();
        }
    }

    pub fn scan_now(&self) {
        self.scan_requested.store(true, Ordering::SeqCst);
        self.wake_scan.notify_one();
    }

    // --- HandBrake ------------------------------------------------------------

    pub async fn test_handbrake(&self, exe: &str) -> Result<String> {
        let (_, text) = capture(exe, &["--version".into()], Duration::from_secs(20)).await?;
        text.lines()
            .map(str::trim)
            .find(|l| l.to_ascii_lowercase().contains("handbrake"))
            .map(str::to_string)
            .or_else(|| text.lines().map(str::trim).find(|l| !l.is_empty()).map(str::to_string))
            .ok_or_else(|| Error::Msg("HandBrakeCLI ran but printed no version.".into()))
    }

    pub async fn list_presets(&self, exe: &str) -> Result<Vec<String>> {
        let (_, text) = capture(exe, &["--preset-list".into()], Duration::from_secs(30)).await?;
        let presets = parse_preset_list(&text);
        if presets.is_empty() {
            return Err(Error::Msg("HandBrakeCLI listed no presets.".into()));
        }
        Ok(presets)
    }

    async fn check_handbrake(&self) {
        let exe = self.settings().handbrake_path;
        match self.test_handbrake(&exe).await {
            Ok(v) => {
                self.log("handbrake", &format!("{v} at {exe}"), false);
                self.set_status(|s| s.handbrake_version = Some(v));
            }
            Err(e) => {
                self.log("handbrake", &e.to_string(), true);
                self.set_status(|s| s.handbrake_version = None);
            }
        }
    }

    /// Size and length of a source (`--scan`). Empty when it can't be read.
    async fn probe(&self, exe: &str, path: &Path) -> VideoInfo {
        let args = vec!["--scan".to_string(), "--title".into(), "0".into(), "--input".into(), path.to_string_lossy().to_string()];
        match capture(exe, &args, PROBE_TIMEOUT).await {
            Ok((_, text)) => {
                let info = parse_scan(&text);
                self.log(
                    "scan",
                    &format!(
                        "{}: {}{}",
                        path.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default(),
                        match (info.width, info.height) {
                            (Some(w), Some(h)) => format!("{w}x{h} ({})", info.category()),
                            _ => "resolution unknown".into(),
                        },
                        info.duration_secs.map(|d| format!(", {}:{:02}:{:02}", d / 3600, (d / 60) % 60, d % 60)).unwrap_or_default()
                    ),
                    false,
                );
                info
            }
            Err(e) => {
                self.log("scan", &format!("Couldn't read {}: {e}", path.display()), true);
                VideoInfo::default()
            }
        }
    }

    // --- server ---------------------------------------------------------------

    async fn restart_server(self: &Arc<Self>) {
        if let Some(h) = self.server.lock().await.take() {
            h.stop().await;
            self.log("server", "Upload server stopped.", false);
        }
        let s = self.settings();
        if !s.server_enabled {
            self.set_status(|st| st.server = ServerStatus { running: false, address: s.listen_address.clone(), error: None });
            return;
        }
        match server::start(Arc::clone(self), &s.listen_address, s.max_upload_bytes).await {
            Ok(h) => {
                *self.server.lock().await = Some(h);
                self.log("server", &format!("Listening on http://{} (POST /upload, GET /health).", s.listen_address), false);
                self.set_status(|st| st.server = ServerStatus { running: true, address: s.listen_address.clone(), error: None });
            }
            Err(e) => {
                self.log("server", &e.to_string(), true);
                self.set_status(|st| st.server = ServerStatus { running: false, address: s.listen_address.clone(), error: Some(e.to_string()) });
            }
        }
    }

    // --- loops ---------------------------------------------------------------

    /// Start the server and both loops. Runs until the process ends.
    pub async fn run(self: Arc<Self>) {
        self.check_handbrake().await;
        self.restart_server().await;
        let scan = Arc::clone(&self);
        tokio::spawn(async move { scan.scan_loop().await });
        self.process_loop().await;
    }

    async fn scan_loop(self: Arc<Self>) {
        let mut first = true;
        loop {
            let s = self.settings();
            let requested = self.scan_requested.swap(false, Ordering::SeqCst);
            if first || requested || s.monitoring_enabled {
                first = false;
                self.scan_folders().await;
            }
            let interval = Duration::from_secs(u64::from(s.scan_interval_minutes) * 60);
            self.set_status(|st| st.next_scan_ms = s.monitoring_enabled.then(|| now_ms() + interval.as_millis() as i64));
            tokio::select! {
                _ = self.wake_scan.notified() => {}
                _ = tokio::time::sleep(interval) => {}
            }
        }
    }

    async fn scan_folders(&self) {
        self.scanning.store(true, Ordering::SeqCst);
        self.refresh_status();
        let settings = self.settings();
        let mut found_total = 0;
        for folder in settings.folders.iter().filter(|f| f.problems().is_empty()) {
            let root = PathBuf::from(&folder.input_path);
            if !root.is_dir() {
                self.log("scan", &format!("{} isn't there (or isn't reachable): {}", folder.name, folder.input_path), true);
                continue;
            }
            let f = folder.clone();
            let r = root.clone();
            let found = tokio::task::spawn_blocking(move || find_files(&r, f.recursive, MIN_FILE_AGE, |p, size| f.accepts(p, size))).await.unwrap_or_default();
            for file in found {
                let path_str = file.path.to_string_lossy().to_string();
                if is_in_moved(&file.path) || Self::has_job_for(&self.jobs(), &path_str) {
                    continue;
                }
                let info = self.probe(&settings.handbrake_path, &file.path).await;
                let output_dir = output_dir_for(&root, &file.path, Path::new(&folder.output_path));
                let job = self.job_for(folder, &file.path, file.size_bytes, &info, output_dir, JobSource::Folder);
                if self.enqueue(job).is_some() {
                    found_total += 1;
                }
            }
        }
        if found_total > 0 {
            self.log("scan", &format!("Queued {found_total} new file(s)."), false);
        }
        self.scanning.store(false, Ordering::SeqCst);
        self.refresh_status();
    }

    fn next_pending(&self) -> Option<String> {
        self.jobs.lock().expect("jobs lock").iter().find(|j| j.status == JobStatus::Pending).map(|j| j.id.clone())
    }

    async fn process_loop(self: Arc<Self>) {
        loop {
            while !self.paused.load(Ordering::SeqCst) {
                let Some(id) = self.next_pending() else { break };
                let settings = self.settings();
                if settings.window().is_some_and(|w| !w.contains(now_clock())) {
                    break;
                }
                if !Path::new(&settings.handbrake_path).is_file() && settings.handbrake_path.contains(['/', '\\']) {
                    self.log("handbrake", &format!("HandBrakeCLI wasn't found at {}: fix the path in Settings.", settings.handbrake_path), true);
                    break;
                }
                self.run_job(&id).await;
            }
            self.refresh_status();
            tokio::select! {
                _ = self.wake_process.notified() => {}
                _ = tokio::time::sleep(Duration::from_secs(15)) => {}
            }
        }
    }

    /// Update a running job's progress and tell the UI (throttled).
    fn progress(&self, id: &str, f: impl FnOnce(&mut JobProgress)) {
        let Some(job) = self.update_job(id, |j| f(&mut j.progress)) else { return };
        let due = {
            let mut last = self.last_progress_emit.lock().expect("emit lock");
            if last.elapsed() >= EMIT_EVERY || job.progress.fraction >= 1.0 {
                *last = Instant::now();
                true
            } else {
                false
            }
        };
        if due {
            self.sink.job_progress(&job);
        }
    }

    async fn run_job(self: &Arc<Self>, id: &str) {
        let cancel = CancellationToken::new();
        *self.current.lock().expect("current lock") = Some((id.to_string(), cancel.clone()));
        let Some(mut job) = self.update_job(id, |j| {
            j.status = JobStatus::Processing;
            j.started_ms = Some(now_ms());
            j.finished_ms = None;
            j.attempts += 1;
            j.error = None;
            j.output_file = None;
            j.progress = JobProgress::default();
        }) else {
            *self.current.lock().expect("current lock") = None;
            return;
        };
        self.jobs_updated();
        let name = job.file_name();
        self.log("queue", &format!("Starting {name} (try {}) with preset \"{}\"", job.attempts, job.preset), false);

        let mut work = Work::default();
        let result = self.process(&mut job, &cancel, &mut work).await;

        if let Some(t) = &work.temp_input {
            let _ = std::fs::remove_file(t);
        }
        if result.is_err() {
            if let Some(e) = &work.encode {
                let _ = std::fs::remove_file(e);
            }
        }

        let cancelled = cancel.is_cancelled();
        let paused = self.paused.load(Ordering::SeqCst);
        let max_attempts = self.settings().max_attempts;
        let mut requeue = false;
        let finished = self.update_job(id, |j| {
            j.input_path = job.input_path.clone();
            j.progress = JobProgress::default();
            j.finished_ms = Some(now_ms());
            match &result {
                Ok(out) => {
                    j.status = JobStatus::Completed;
                    j.output_file = Some(out.to_string_lossy().to_string());
                }
                Err(_) if cancelled && paused => {
                    j.status = JobStatus::Pending;
                    j.attempts -= 1;
                    j.started_ms = None;
                    j.finished_ms = None;
                }
                Err(_) if cancelled => {
                    j.status = JobStatus::Cancelled;
                    j.error = Some("Cancelled.".into());
                }
                Err(JobError::Fatal(e)) => {
                    j.status = JobStatus::Failed;
                    j.error = Some(e.clone());
                }
                Err(JobError::Retry(e)) => {
                    j.error = Some(e.clone());
                    if j.attempts < max_attempts {
                        j.status = JobStatus::Pending;
                        j.started_ms = None;
                        j.finished_ms = None;
                        requeue = true;
                    } else {
                        j.status = JobStatus::Failed;
                    }
                }
            }
        });
        if requeue {
            // To the back of the queue, so the others get their turn first.
            let mut jobs = self.jobs.lock().expect("jobs lock");
            if let Some(pos) = jobs.iter().position(|j| j.id == id) {
                let j = jobs.remove(pos);
                jobs.push(j);
            }
        }
        *self.current.lock().expect("current lock") = None;
        self.jobs_updated();

        match (&result, finished) {
            (Ok(out), _) => {
                self.log("queue", &format!("Finished {name} → {}", out.display()), false);
                self.sink.notify("Encode finished", &name);
            }
            (Err(_), Some(j)) if j.status == JobStatus::Pending => {
                self.log("queue", &format!("{name}: {} (will try again, {}/{max_attempts})", j.error.clone().unwrap_or_default(), j.attempts), true);
            }
            (Err(_), Some(j)) if j.status == JobStatus::Failed => {
                let why = j.error.clone().unwrap_or_default();
                self.log("queue", &format!("{name} failed: {why} (kept in {})", j.input_path), true);
                self.sink.notify("Encode failed", &format!("{name}: {why}"));
            }
            _ => {}
        }
    }

    async fn process(self: &Arc<Self>, job: &mut Job, cancel: &CancellationToken, work: &mut Work) -> std::result::Result<PathBuf, JobError> {
        let settings = self.settings();
        let id = job.id.clone();
        let name = job.file_name();
        let src = PathBuf::from(&job.input_path);
        if !src.is_file() {
            return Err(JobError::Fatal(format!("The source file isn't there any more: {}", src.display())));
        }

        // Park the source in `_moved` so the next scan doesn't find it again.
        match park_in_moved(&src) {
            Ok(p) if p != src => {
                self.log(&name, &format!("Moved to {}", p.display()), false);
                job.input_path = p.to_string_lossy().to_string();
            }
            Ok(_) => {}
            Err(e) => self.log(&name, &format!("Couldn't move it to _moved ({e}); encoding it where it is."), true),
        }
        let src = PathBuf::from(&job.input_path);
        let output_dir = PathBuf::from(&job.output_dir);
        std::fs::create_dir_all(&output_dir).map_err(|e| JobError::Retry(format!("Couldn't create the output folder {}: {e}", output_dir.display())))?;

        // Staging: copy the source somewhere local and encode there.
        let mut encode_input = src.clone();
        let mut encode_dir = output_dir.clone();
        if !job.staging_dir.is_empty() {
            let staging = PathBuf::from(&job.staging_dir);
            std::fs::create_dir_all(&staging).map_err(|e| JobError::Retry(format!("Couldn't create the staging folder {}: {e}", staging.display())))?;
            encode_dir = staging.clone();
            let needed = job.size_bytes + job.size_bytes / 2;
            match free_space(&staging) {
                Some(free) if free < needed => {
                    self.log(&name, &format!("Only {:.1} GB free in {}: encoding straight from the source.", free as f64 / 1e9, staging.display()), true);
                }
                _ => {
                    let temp = staging.join(format!("{}_{name}", job.id));
                    work.temp_input = Some(temp.clone());
                    self.log(&name, &format!("Copying to {}", temp.display()), false);
                    self.progress(&id, |p| *p = JobProgress { phase: JobPhase::Staging, ..Default::default() });
                    self.copy_with_progress(&id, &src, &temp, cancel, JobPhase::Staging).await?;
                    encode_input = temp;
                }
            }
        }
        work.encode_dir = Some(encode_dir.clone());

        if !is_writable_dir(&encode_dir) {
            self.log(
                &name,
                &format!("Can't write to {}: check the volume is mounted and not read-only. HandBrake will fail with avio_open2 if so.", encode_dir.display()),
                true,
            );
        }

        // Encode.
        let encode_file = encode_dir.join(output_name(&src));
        if encode_file.exists() {
            std::fs::remove_file(&encode_file).map_err(|e| JobError::Retry(format!("Couldn't replace {}: {e}", encode_file.display())))?;
        }
        work.encode = Some(encode_file.clone());
        let args = vec![
            "--input".to_string(),
            encode_input.to_string_lossy().to_string(),
            "--output".into(),
            encode_file.to_string_lossy().to_string(),
            "--preset".into(),
            job.preset.clone(),
            "--verbose=1".into(),
        ];
        self.log(&name, &format!("Encoding to {}", encode_file.display()), false);
        self.progress(&id, |p| *p = JobProgress { phase: JobPhase::Encoding, ..Default::default() });
        let mut job_log = settings.keep_job_logs.then(|| {
            let dir = self.logs_dir.join("jobs");
            let _ = std::fs::create_dir_all(&dir);
            std::fs::File::create(dir.join(format!("{id}.log"))).ok()
        }).flatten();
        let mut last_error: Option<String> = None;
        let outcome = run_handbrake(&settings.handbrake_path, &args, ENCODE_STALL, cancel, |line| {
            if let Some(f) = job_log.as_mut() {
                let _ = writeln!(f, "{line}");
            }
            if let Some(p) = parse_progress(line) {
                self.progress(&id, |pr| {
                    pr.phase = JobPhase::Encoding;
                    pr.fraction = (p.percent / 100.0).clamp(0.0, 1.0);
                    pr.fps = p.fps.or(pr.fps);
                    pr.avg_fps = p.avg_fps.or(pr.avg_fps);
                    pr.eta_secs = p.eta_secs.or(pr.eta_secs);
                });
            } else if is_error_line(line) {
                last_error = Some(line.to_string());
                self.log("handbrake", line, true);
            }
        })
        .await
        .map_err(|e| JobError::Retry(e.to_string()))?;
        let detail = last_error.map(|e| format!(": {e}")).unwrap_or_default();
        match outcome {
            Outcome::Finished { code: Some(0) } => {}
            Outcome::Finished { code } => {
                return Err(JobError::Retry(format!("HandBrakeCLI failed (exit code {}){detail}", code.map(|c| c.to_string()).unwrap_or_else(|| "?".into()))));
            }
            Outcome::Stalled => return Err(JobError::Retry(format!("HandBrakeCLI printed nothing for {} minutes and was stopped{detail}", ENCODE_STALL.as_secs() / 60))),
            Outcome::Cancelled => return Err(JobError::Retry("Cancelled.".into())),
        }
        let size = std::fs::metadata(&encode_file).map(|m| m.len()).unwrap_or(0);
        if size == 0 {
            return Err(JobError::Retry(format!("HandBrakeCLI finished but wrote nothing to {}{detail}", encode_file.display())));
        }

        // Deliver from staging to the output folder.
        let final_file = if encode_dir != output_dir {
            let dst = output_dir.join(output_name(&src));
            self.log(&name, &format!("Copying the encode to {}", dst.display()), false);
            self.progress(&id, |p| *p = JobProgress { phase: JobPhase::Delivering, ..Default::default() });
            self.copy_with_progress(&id, &encode_file, &dst, cancel, JobPhase::Delivering).await?;
            let _ = std::fs::remove_file(&encode_file);
            dst
        } else {
            encode_file
        };
        work.encode = None;

        if job.delete_source {
            match std::fs::remove_file(&src) {
                Ok(()) => self.log(&name, "Deleted the source.", false),
                Err(e) => self.log(&name, &format!("Couldn't delete the source {}: {e}", src.display()), true),
            }
        }
        Ok(final_file)
    }

    async fn copy_with_progress(self: &Arc<Self>, id: &str, src: &Path, dst: &Path, cancel: &CancellationToken, phase: JobPhase) -> std::result::Result<(), JobError> {
        let me = Arc::clone(self);
        let (id, src, dst, cancel) = (id.to_string(), src.to_path_buf(), dst.to_path_buf(), cancel.clone());
        tokio::task::spawn_blocking(move || {
            copy_file(&src, &dst, &cancel, |done, total| {
                me.progress(&id, |p| {
                    p.phase = phase;
                    p.fraction = if total > 0 { done as f64 / total as f64 } else { 1.0 };
                });
            })
        })
        .await
        .map_err(|e| JobError::Retry(e.to_string()))?
        .map_err(JobError::from)
    }

    // --- misc ------------------------------------------------------------------

    /// Folders the app may open in the file manager: its own, and the configured ones.
    pub fn is_own_folder(&self, path: &Path) -> bool {
        let s = self.settings();
        let mut roots: Vec<String> = vec![s.incoming_dir.clone(), s.staging_dir.clone(), self.logs_dir.to_string_lossy().to_string()];
        for f in &s.folders {
            roots.extend([f.input_path.clone(), f.output_path.clone(), f.staging_path.clone()]);
        }
        roots.extend(self.jobs().into_iter().flat_map(|j| [j.output_dir, j.staging_dir]));
        roots.iter().filter(|r| !r.trim().is_empty()).any(|r| path.starts_with(r))
    }

    pub fn has_running_job(&self) -> bool {
        self.current.lock().expect("current lock").is_some()
    }

    /// Stop the running encode (it goes back to the queue) and the server.
    pub async fn shutdown(&self) {
        self.paused.store(true, Ordering::SeqCst);
        if let Some((_, token)) = self.current.lock().expect("current lock").as_ref() {
            token.cancel();
        }
        let deadline = Instant::now() + Duration::from_secs(5);
        while self.has_running_job() && Instant::now() < deadline {
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        if let Some(h) = self.server.lock().await.take() {
            h.stop().await;
        }
    }
}
