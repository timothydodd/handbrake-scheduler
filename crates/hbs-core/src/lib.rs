//! Pure logic for HandbrakeScheduler: no IO, no processes, no Tauri.
//! Everything here is unit-tested; `hbs-engine` does the IO around it.

pub mod handbrake;
pub mod job;
pub mod paths;
pub mod routing;
pub mod schedule;
pub mod settings;
pub mod transfer;

pub use handbrake::{parse_preset_list, parse_progress, parse_scan, Progress, VideoInfo};
pub use job::{Job, JobPhase, JobProgress, JobSource, JobStatus};
pub use schedule::{in_window, parse_clock, Clock, TimeWindow};
pub use settings::{FolderSetting, ResolutionPreset, Settings};

/// Milliseconds since the Unix epoch.
pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}
