//! Which watched folder's settings an upload is processed with. The old
//! scheduler matched the relative path AutoMk sent (`Movies\…`, `TV Shows\…`)
//! against the input paths of the configured folders; kept as is.

use std::path::{Path, PathBuf};

use crate::paths::safe_relative;
use crate::settings::FolderSetting;

/// The folder whose input path mentions the upload's kind ("Movie" / "TV" or
/// "Series"), else the first folder.
pub fn route_upload<'a>(folders: &'a [FolderSetting], relative_path: &str) -> Option<&'a FolderSetting> {
    let rel = relative_path.to_ascii_lowercase();
    let wanted: &[&str] = if rel.contains("movie") {
        &["movie"]
    } else if rel.contains("tv") || rel.contains("series") {
        &["tv", "series"]
    } else {
        &[]
    };
    let by_kind = folders.iter().find(|f| {
        let hay = format!("{} {}", f.input_path, f.name).to_ascii_lowercase();
        wanted.iter().any(|w| hay.contains(w))
    });
    by_kind.or_else(|| folders.first())
}

/// Where an upload's encode goes: the folder's output root plus the folder
/// part of the relative path it was sent with.
pub fn upload_output_dir(folder: &FolderSetting, relative_path: &str) -> PathBuf {
    let root = Path::new(&folder.output_path);
    match safe_relative(relative_path).and_then(|p| p.parent().map(Path::to_path_buf)) {
        Some(sub) => root.join(sub),
        None => root.to_path_buf(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn f(name: &str, input: &str, output: &str) -> FolderSetting {
        FolderSetting { name: name.into(), input_path: input.into(), output_path: output.into(), ..Default::default() }
    }

    #[test]
    fn routes_by_kind() {
        let folders = vec![f("Movies", r"D:\in\Movies", r"\\nas\Movies"), f("Shows", r"D:\in\TV Shows", r"\\nas\TV")];
        assert_eq!(route_upload(&folders, r"Movies\A (2000)\A.mkv").unwrap().name, "Movies");
        assert_eq!(route_upload(&folders, r"TV Shows\B\Season 01\x.mkv").unwrap().name, "Shows");
        assert_eq!(route_upload(&folders, "other/x.mkv").unwrap().name, "Movies");
        assert!(route_upload(&[], "x").is_none());
    }

    #[test]
    fn output_dirs() {
        let folder = f("Movies", r"D:\in", "/out");
        assert_eq!(upload_output_dir(&folder, r"Movies\A (2000)\A (2000).mkv"), PathBuf::from("/out/Movies/A (2000)"));
        assert_eq!(upload_output_dir(&folder, "A.mkv"), PathBuf::from("/out"));
        assert_eq!(upload_output_dir(&folder, "../A.mkv"), PathBuf::from("/out"));
    }
}
