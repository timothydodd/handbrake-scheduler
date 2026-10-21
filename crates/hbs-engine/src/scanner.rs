//! Walking a watched folder for video files.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

use hbs_core::paths::MOVED_DIR;

/// A file a watched folder turned up.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord)]
pub struct Found {
    pub path: PathBuf,
    pub size_bytes: u64,
}

/// Files under `root` (recursively when asked), skipping `_moved` folders,
/// hidden entries, files still being written (`.part`, or changed within
/// `min_age`) and anything `accept` turns down. Unreadable folders are skipped.
pub fn find_files(root: &Path, recursive: bool, min_age: Duration, accept: impl Fn(&Path, u64) -> bool) -> Vec<Found> {
    let mut out = Vec::new();
    let mut dirs = vec![root.to_path_buf()];
    let now = SystemTime::now();
    while let Some(dir) = dirs.pop() {
        let Ok(entries) = fs::read_dir(&dir) else { continue };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().to_string();
            if name.starts_with('.') {
                continue;
            }
            let Ok(meta) = entry.metadata() else { continue };
            if meta.is_dir() {
                if recursive && name != MOVED_DIR {
                    dirs.push(path);
                }
                continue;
            }
            if !meta.is_file() || name.ends_with(".part") || name.ends_with(".tmp") {
                continue;
            }
            if let Ok(modified) = meta.modified() {
                if now.duration_since(modified).map_or(false, |age| age < min_age) {
                    continue;
                }
            }
            if accept(&path, meta.len()) {
                out.push(Found { path, size_bytes: meta.len() });
            }
        }
    }
    out.sort();
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn walks_and_skips() {
        let d = std::env::temp_dir().join(format!("hbs-scan-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(d.join("sub/_moved")).unwrap();
        fs::create_dir_all(d.join(".hidden")).unwrap();
        for f in ["a.mkv", "b.txt", "sub/c.mkv", "sub/_moved/d.mkv", "e.mkv.part", ".hidden/f.mkv"] {
            fs::write(d.join(f), b"xx").unwrap();
        }
        let is_mkv = |p: &Path, _: u64| p.extension().is_some_and(|e| e == "mkv");
        let all = find_files(&d, true, Duration::ZERO, is_mkv);
        let names: Vec<String> = all.iter().map(|f| f.path.strip_prefix(&d).unwrap().to_string_lossy().replace('\\', "/")).collect();
        assert_eq!(names, vec!["a.mkv", "sub/c.mkv"]);
        assert_eq!(find_files(&d, false, Duration::ZERO, is_mkv).len(), 1);
        assert!(find_files(&d, true, Duration::from_secs(3600), is_mkv).is_empty());
        assert!(find_files(&d.join("missing"), true, Duration::ZERO, is_mkv).is_empty());
        fs::remove_dir_all(&d).unwrap();
    }
}
