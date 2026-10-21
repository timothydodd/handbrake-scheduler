//! A transcode job: one source file, where its encode goes, and how far it got.
//! The queue is saved as `jobs.json` in the app data folder.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum JobStatus {
    #[default]
    Pending,
    Processing,
    Completed,
    Failed,
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum JobSource {
    /// Found in a watched folder.
    #[default]
    Folder,
    /// Received over HTTP from AutoMk.
    Upload,
    /// Added by hand in the app.
    Manual,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum JobPhase {
    #[default]
    Waiting,
    /// Copying the source into the staging folder.
    Staging,
    Encoding,
    /// Copying the encode from staging to the output folder.
    Delivering,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct JobProgress {
    pub phase: JobPhase,
    /// 0..=1 within the phase.
    pub fraction: f64,
    pub fps: Option<f64>,
    pub avg_fps: Option<f64>,
    pub eta_secs: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Job {
    pub id: String,
    pub source: JobSource,
    /// Where the file is now (moves into `_moved` when processing starts).
    pub input_path: String,
    /// Where it was found.
    pub original_path: String,
    /// Name of the watched folder (or "Uploads") it came through.
    pub folder_name: String,
    pub output_dir: String,
    pub preset: String,
    pub delete_source: bool,
    /// Staging folder to encode in; empty = encode straight into the output folder.
    pub staging_dir: String,
    pub size_bytes: u64,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub created_ms: i64,
    pub started_ms: Option<i64>,
    pub finished_ms: Option<i64>,
    pub status: JobStatus,
    /// Tries so far (1 after the first run).
    pub attempts: u32,
    pub error: Option<String>,
    /// The finished encode.
    pub output_file: Option<String>,
    #[serde(skip_serializing_if = "is_default_progress")]
    pub progress: JobProgress,
}

fn is_default_progress(p: &JobProgress) -> bool {
    *p == JobProgress::default()
}

impl Default for Job {
    fn default() -> Self {
        Self {
            id: String::new(),
            source: JobSource::Folder,
            input_path: String::new(),
            original_path: String::new(),
            folder_name: String::new(),
            output_dir: String::new(),
            preset: String::new(),
            delete_source: false,
            staging_dir: String::new(),
            size_bytes: 0,
            width: None,
            height: None,
            created_ms: 0,
            started_ms: None,
            finished_ms: None,
            status: JobStatus::Pending,
            attempts: 0,
            error: None,
            output_file: None,
            progress: JobProgress::default(),
        }
    }
}

impl Job {
    pub fn file_name(&self) -> String {
        std::path::Path::new(&self.input_path)
            .file_name()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_else(|| self.input_path.clone())
    }

    pub fn is_open(&self) -> bool {
        matches!(self.status, JobStatus::Pending | JobStatus::Processing)
    }

    pub fn is_finished(&self) -> bool {
        !self.is_open()
    }
}

/// On load, a job that was processing when the app last ran goes back to the queue.
pub fn recover(jobs: &mut [Job]) {
    for j in jobs {
        if j.status == JobStatus::Processing {
            j.status = JobStatus::Pending;
            j.started_ms = None;
            j.progress = JobProgress::default();
        }
    }
}

/// Keep every open job and the newest `keep_finished` finished ones.
pub fn prune(jobs: &mut Vec<Job>, keep_finished: usize) {
    let mut finished = 0;
    let mut i = jobs.len();
    while i > 0 {
        i -= 1;
        if jobs[i].is_finished() {
            finished += 1;
            if finished > keep_finished {
                jobs.remove(i);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn job(status: JobStatus) -> Job {
        Job { status, ..Default::default() }
    }

    #[test]
    fn recovers_and_prunes() {
        let mut jobs = vec![job(JobStatus::Completed), job(JobStatus::Processing), job(JobStatus::Failed), job(JobStatus::Pending), job(JobStatus::Completed)];
        recover(&mut jobs);
        assert_eq!(jobs[1].status, JobStatus::Pending);
        prune(&mut jobs, 2);
        assert_eq!(jobs.len(), 4);
        assert_eq!(jobs.iter().filter(|j| j.is_finished()).count(), 2);
        assert_eq!(jobs[0].status, JobStatus::Pending); // the oldest finished one went
    }

    #[test]
    fn tolerates_old_json() {
        let j: Job = serde_json::from_str(r#"{"id":"a","inputPath":"/x/y.mkv","unknown":1}"#).unwrap();
        assert_eq!(j.file_name(), "y.mkv");
        assert!(j.is_open());
        let s = serde_json::to_string(&j).unwrap();
        assert!(!s.contains("progress"));
    }
}
