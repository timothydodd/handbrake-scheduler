//! HandbrakeScheduler engine: everything with side effects (HandBrakeCLI
//! processes, folder scans, the upload server, the file system). Talks to the
//! UI through [`EventSink`].

pub mod engine;
pub mod files;
pub mod runner;
pub mod scanner;
pub mod server;

pub use engine::{Engine, EngineState, EngineStatus, LogLine, ServerStatus};

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    Msg(String),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

pub type Result<T> = std::result::Result<T, Error>;

/// How the engine reports to the UI (the Tauri layer forwards these as events).
pub trait EventSink: Send + Sync + 'static {
    /// The whole queue (something was added, finished, removed or reordered).
    fn jobs_changed(&self, jobs: &[hbs_core::Job]);
    /// One job's progress moved (throttled).
    fn job_progress(&self, job: &hbs_core::Job);
    fn status_changed(&self, status: &EngineStatus);
    fn log(&self, line: &LogLine);
    fn notify(&self, title: &str, body: &str);
}
