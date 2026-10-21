//! Path rules shared by the scanner, the server and the encoder.

use std::path::{Component, Path, PathBuf};

/// Folder processed sources are parked in, next to where they were found.
pub const MOVED_DIR: &str = "_moved";

/// Encodes are MP4 named after the source.
pub fn output_name(input: &Path) -> String {
    format!("{}.mp4", input.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_else(|| "output".into()))
}

/// `<output root>/<source's folder relative to the watched folder>`.
pub fn output_dir_for(watched_root: &Path, source: &Path, output_root: &Path) -> PathBuf {
    let rel = source.parent().and_then(|p| p.strip_prefix(watched_root).ok()).unwrap_or(Path::new(""));
    output_root.join(rel)
}

/// Whether any folder on the path is the moved folder.
pub fn is_in_moved(path: &Path) -> bool {
    path.components().any(|c| matches!(c, Component::Normal(n) if n == MOVED_DIR))
}

/// A relative path sent by a client, made safe to join under a root: no
/// drive, no root, no `..`; backslashes count as separators. `None` when
/// nothing usable is left.
pub fn safe_relative(rel: &str) -> Option<PathBuf> {
    let mut out = PathBuf::new();
    for part in rel.split(['/', '\\']) {
        let part = part.trim();
        if part.is_empty() || part == "." {
            continue;
        }
        if part == ".." || part.contains(':') {
            return None;
        }
        out.push(part);
    }
    (out.as_os_str().len() > 0).then_some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_and_dirs() {
        assert_eq!(output_name(Path::new("/a/b/Movie (2000).mkv")), "Movie (2000).mp4");
        assert_eq!(output_dir_for(Path::new("/in"), Path::new("/in/Movies/x/a.mkv"), Path::new("/out")), PathBuf::from("/out/Movies/x"));
        assert_eq!(output_dir_for(Path::new("/in"), Path::new("/in/a.mkv"), Path::new("/out")), PathBuf::from("/out"));
        assert_eq!(output_dir_for(Path::new("/in"), Path::new("/elsewhere/a.mkv"), Path::new("/out")), PathBuf::from("/out"));
        assert!(is_in_moved(Path::new("/in/_moved/a.mkv")));
        assert!(!is_in_moved(Path::new("/in/_moved2/a.mkv")));
    }

    #[test]
    fn safe_relatives() {
        assert_eq!(safe_relative(r"Movies\A (2000)\A (2000).mkv"), Some(PathBuf::from("Movies/A (2000)/A (2000).mkv")));
        assert_eq!(safe_relative("/TV Shows//x.mkv"), Some(PathBuf::from("TV Shows/x.mkv")));
        assert_eq!(safe_relative("../x.mkv"), None);
        assert_eq!(safe_relative(r"C:\x.mkv"), None);
        assert_eq!(safe_relative(""), None);
    }
}
