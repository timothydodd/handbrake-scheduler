//! App settings, saved as `settings.json` in the app data folder.
//!
//! Mirrors the old `appsettings.json` (`HandBrake` + `FileTransfer` sections)
//! with the same meaning for each field, so an old config maps over one to one.

use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::schedule::{parse_clock, TimeWindow};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    /// `HandBrakeCLI.exe` / `HandBrakeCLI`.
    pub handbrake_path: String,
    /// Preset for folders that don't name one.
    pub default_preset: String,
    /// Watched folders. Files found in them become jobs.
    pub folders: Vec<FolderSetting>,
    /// Keep scanning the folders; off = scan once at startup (and on "Scan now").
    pub monitoring_enabled: bool,
    pub scan_interval_minutes: u32,
    /// Only transcode between these local times ("22:00" to "06:00" spans midnight). Both empty = always.
    pub start_time: String,
    pub end_time: String,
    /// Where the HandBrake CLI output of each job is kept (one file per job).
    pub keep_job_logs: bool,
    /// Accept uploads from AutoMk over HTTP.
    pub server_enabled: bool,
    /// `host:port` the upload server listens on.
    pub listen_address: String,
    /// Where uploads land (then they're queued like a watched file).
    pub incoming_dir: String,
    pub max_upload_bytes: u64,
    /// Default staging folder for folders that stage but don't name one; empty = the system temp folder.
    pub staging_dir: String,
    /// Give a failed job this many tries in all.
    pub max_attempts: u32,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            handbrake_path: default_handbrake_path().into(),
            default_preset: "HQ 1080p30 Surround".into(),
            folders: Vec::new(),
            monitoring_enabled: true,
            scan_interval_minutes: 1,
            start_time: String::new(),
            end_time: String::new(),
            keep_job_logs: false,
            server_enabled: true,
            listen_address: "0.0.0.0:5000".into(),
            incoming_dir: String::new(),
            max_upload_bytes: 100 << 30,
            staging_dir: String::new(),
            max_attempts: 3,
        }
    }
}

pub fn default_handbrake_path() -> &'static str {
    if cfg!(windows) {
        r"C:\Program Files\HandBrake\HandBrakeCLI.exe"
    } else {
        "HandBrakeCLI"
    }
}

impl Settings {
    /// Fill empty folders from a base (the user's Videos folder) and clamp numbers.
    pub fn normalize(&mut self, videos_dir: &Path) {
        let base = videos_dir.join("HandbrakeScheduler");
        if self.incoming_dir.trim().is_empty() {
            self.incoming_dir = base.join("incoming").to_string_lossy().to_string();
        }
        if self.handbrake_path.trim().is_empty() {
            self.handbrake_path = default_handbrake_path().into();
        }
        if self.default_preset.trim().is_empty() {
            self.default_preset = Settings::default().default_preset;
        }
        self.listen_address = self.listen_address.trim().to_string();
        if self.listen_address.is_empty() {
            self.listen_address = "0.0.0.0:5000".into();
        } else if !self.listen_address.contains(':') {
            self.listen_address = format!("0.0.0.0:{}", self.listen_address);
        }
        self.scan_interval_minutes = self.scan_interval_minutes.clamp(1, 24 * 60);
        self.max_attempts = self.max_attempts.clamp(1, 10);
        if self.max_upload_bytes == 0 {
            self.max_upload_bytes = 100 << 30;
        }
        self.start_time = self.start_time.trim().to_string();
        self.end_time = self.end_time.trim().to_string();
        for f in &mut self.folders {
            f.normalize(&self.default_preset);
        }
    }

    /// The processing window, when both times are set and valid.
    pub fn window(&self) -> Option<TimeWindow> {
        let start = parse_clock(&self.start_time)?;
        let end = parse_clock(&self.end_time)?;
        (start != end).then_some(TimeWindow { start, end })
    }

    /// What still needs setting up (shown in the status bar).
    pub fn problems(&self) -> Vec<String> {
        let mut p = Vec::new();
        if self.folders.is_empty() && !self.server_enabled {
            p.push("Nothing to do: add a watched folder or turn the upload server on.".into());
        }
        let set = |v: &str| !v.is_empty();
        if set(&self.start_time) != set(&self.end_time) {
            p.push("Set both a start and an end time for the processing window, or neither.".into());
        } else if set(&self.start_time) && self.window().is_none() {
            p.push("The processing window isn't valid: use HH:MM times that differ.".into());
        }
        for (i, f) in self.folders.iter().enumerate() {
            for e in f.problems() {
                p.push(format!("Folder {}: {e}", i + 1));
            }
        }
        p
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FolderSetting {
    /// Shown in the UI; defaults to the input folder's name.
    pub name: String,
    pub input_path: String,
    pub output_path: String,
    pub preset: String,
    /// Lower-case with the dot: `.mkv`.
    pub file_extensions: Vec<String>,
    /// Delete the source (from `_moved`) once the encode succeeded.
    pub delete_source: bool,
    pub recursive: bool,
    /// Copy the source to a local staging folder and encode there, then copy the result out.
    pub use_staging: bool,
    /// Empty = the app-wide staging folder (or the system temp folder).
    pub staging_path: String,
    pub min_file_size_bytes: u64,
    /// 0 = no limit.
    pub max_file_size_bytes: u64,
    /// Pick a different preset by source resolution.
    pub resolution_presets: Vec<ResolutionPreset>,
}

impl Default for FolderSetting {
    fn default() -> Self {
        Self {
            name: String::new(),
            input_path: String::new(),
            output_path: String::new(),
            preset: String::new(),
            file_extensions: default_extensions(),
            delete_source: false,
            recursive: true,
            use_staging: true,
            staging_path: String::new(),
            min_file_size_bytes: 0,
            max_file_size_bytes: 0,
            resolution_presets: Vec::new(),
        }
    }
}

pub fn default_extensions() -> Vec<String> {
    [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".m2ts"].iter().map(|s| s.to_string()).collect()
}

impl FolderSetting {
    pub fn normalize(&mut self, default_preset: &str) {
        self.input_path = self.input_path.trim().to_string();
        self.output_path = self.output_path.trim().to_string();
        self.preset = self.preset.trim().to_string();
        if self.preset.is_empty() {
            self.preset = default_preset.to_string();
        }
        if self.name.trim().is_empty() {
            self.name = Path::new(&self.input_path)
                .file_name()
                .map(|n| n.to_string_lossy().to_string())
                .filter(|n| !n.is_empty())
                .unwrap_or_else(|| self.input_path.clone());
        }
        let mut exts: Vec<String> = self
            .file_extensions
            .iter()
            .map(|e| e.trim().trim_start_matches('.').to_ascii_lowercase())
            .filter(|e| !e.is_empty())
            .map(|e| format!(".{e}"))
            .collect();
        exts.dedup();
        if exts.is_empty() {
            exts = default_extensions();
        }
        self.file_extensions = exts;
        for r in &mut self.resolution_presets {
            r.preset = r.preset.trim().to_string();
        }
        self.resolution_presets.retain(|r| !r.preset.is_empty());
    }

    pub fn problems(&self) -> Vec<String> {
        let mut p = Vec::new();
        if self.input_path.is_empty() {
            p.push("no input folder".into());
        }
        if self.output_path.is_empty() {
            p.push("no output folder".into());
        } else if !self.input_path.is_empty() && same_path(&self.input_path, &self.output_path) {
            p.push("input and output are the same folder".into());
        }
        if self.max_file_size_bytes != 0 && self.max_file_size_bytes <= self.min_file_size_bytes {
            p.push("the maximum file size isn't above the minimum".into());
        }
        p
    }

    /// Whether a file found in the input folder should be queued.
    pub fn accepts(&self, path: &Path, size_bytes: u64) -> bool {
        if size_bytes < self.min_file_size_bytes {
            return false;
        }
        if self.max_file_size_bytes != 0 && size_bytes > self.max_file_size_bytes {
            return false;
        }
        let ext = match path.extension() {
            Some(e) => format!(".{}", e.to_string_lossy().to_ascii_lowercase()),
            None => return false,
        };
        self.file_extensions.iter().any(|e| *e == ext)
    }

    /// The preset to use for a source of the given size: the most demanding
    /// matching resolution rule, else the folder's preset.
    pub fn preset_for(&self, width: Option<u32>, height: Option<u32>) -> String {
        let (Some(w), Some(h)) = (width, height) else { return self.preset.clone() };
        self.resolution_presets
            .iter()
            .filter(|r| h >= r.min_height && r.max_height.is_none_or(|m| h <= m))
            .filter(|r| w >= r.min_width && r.max_width.is_none_or(|m| w <= m))
            .max_by_key(|r| (r.min_height, r.min_width))
            .map(|r| r.preset.clone())
            .unwrap_or_else(|| self.preset.clone())
    }
}

fn same_path(a: &str, b: &str) -> bool {
    let norm = |s: &str| s.replace('\\', "/").trim_end_matches('/').to_ascii_lowercase();
    norm(a) == norm(b)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ResolutionPreset {
    pub min_width: u32,
    pub max_width: Option<u32>,
    pub min_height: u32,
    pub max_height: Option<u32>,
    pub preset: String,
    pub description: String,
}

impl Default for ResolutionPreset {
    fn default() -> Self {
        Self { min_width: 0, max_width: None, min_height: 0, max_height: None, preset: String::new(), description: String::new() }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn folder() -> FolderSetting {
        let mut f = FolderSetting {
            input_path: r"E:\Videos".into(),
            output_path: r"G:\videos".into(),
            preset: "HQ 1080p30 Surround".into(),
            resolution_presets: vec![
                ResolutionPreset { min_width: 3840, min_height: 2160, preset: "4K".into(), ..Default::default() },
                ResolutionPreset { min_width: 1280, min_height: 720, max_height: Some(1079), preset: "720".into(), ..Default::default() },
            ],
            ..Default::default()
        };
        f.normalize("x");
        f
    }

    #[test]
    fn defaults_and_normalizing() {
        let mut s: Settings = serde_json::from_str(r#"{"folders":[{"inputPath":"/in","outputPath":"/out","fileExtensions":["MKV","mp4",""]}],"listenAddress":"5000"}"#).unwrap();
        s.normalize(Path::new("/home/me/Videos"));
        assert!(s.incoming_dir.ends_with("incoming"));
        assert_eq!(s.listen_address, "0.0.0.0:5000");
        assert_eq!(s.folders[0].preset, "HQ 1080p30 Surround");
        assert_eq!(s.folders[0].name, "in");
        assert_eq!(s.folders[0].file_extensions, vec![".mkv", ".mp4"]);
        assert!(s.problems().is_empty());
    }

    #[test]
    fn window_and_problems() {
        let mut s = Settings { start_time: "22:00".into(), end_time: "06:00:00".into(), ..Default::default() };
        s.normalize(Path::new("/v"));
        assert!(s.window().is_some());
        s.end_time = "22:00".into();
        assert!(s.window().is_none());
        assert!(s.problems().iter().any(|p| p.contains("isn't valid")));
        s.end_time.clear();
        assert!(s.problems().iter().any(|p| p.contains("both")));
    }

    #[test]
    fn accepts_by_extension_and_size() {
        let mut f = folder();
        f.min_file_size_bytes = 10;
        f.max_file_size_bytes = 100;
        assert!(f.accepts(Path::new("a.MKV"), 50));
        assert!(!f.accepts(Path::new("a.txt"), 50));
        assert!(!f.accepts(Path::new("a.mkv"), 5));
        assert!(!f.accepts(Path::new("a.mkv"), 500));
        assert!(!f.accepts(Path::new("noext"), 50));
    }

    #[test]
    fn preset_by_resolution() {
        let f = folder();
        assert_eq!(f.preset_for(Some(3840), Some(2160)), "4K");
        assert_eq!(f.preset_for(Some(1920), Some(1080)), "HQ 1080p30 Surround");
        assert_eq!(f.preset_for(Some(1280), Some(720)), "720");
        assert_eq!(f.preset_for(None, None), "HQ 1080p30 Surround");
    }

    #[test]
    fn folder_problems() {
        let mut f = folder();
        f.output_path = r"e:/Videos/".into();
        assert!(f.problems().iter().any(|p| p.contains("same")));
    }
}
