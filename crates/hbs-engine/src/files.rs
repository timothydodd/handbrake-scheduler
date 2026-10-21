//! File helpers: copies with progress, parking sources in `_moved`, free
//! space, and a writable-folder probe.

use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use hbs_core::paths::MOVED_DIR;
use tokio_util::sync::CancellationToken;

use crate::{Error, Result};

/// Copy `src` to `dst` (via `<dst>.part`), calling `progress(done, total)`.
/// Stops early, cleaning up, when `cancel` fires.
pub fn copy_file(src: &Path, dst: &Path, cancel: &CancellationToken, mut progress: impl FnMut(u64, u64)) -> Result<()> {
    if let Some(parent) = dst.parent() {
        fs::create_dir_all(parent)?;
    }
    let total = fs::metadata(src)?.len();
    let part = dst.with_extension(format!("{}.part", dst.extension().map(|e| e.to_string_lossy().to_string()).unwrap_or_default()));
    let mut copy = || -> Result<()> {
        let mut r = fs::File::open(src)?;
        let mut w = fs::File::create(&part)?;
        let mut buf = vec![0u8; 8 << 20];
        let mut done = 0u64;
        loop {
            if cancel.is_cancelled() {
                return Err(Error::Msg("Cancelled.".into()));
            }
            let n = r.read(&mut buf)?;
            if n == 0 {
                break;
            }
            w.write_all(&buf[..n])?;
            done += n as u64;
            progress(done, total);
        }
        w.sync_all()?;
        drop(w);
        if fs::metadata(&part)?.len() != total {
            return Err(Error::Msg(format!("Copy of {} came out the wrong size.", src.display())));
        }
        if dst.exists() {
            fs::remove_file(dst)?;
        }
        fs::rename(&part, dst)?;
        Ok(())
    };
    if let Err(e) = copy() {
        let _ = fs::remove_file(&part);
        return Err(e);
    }
    Ok(())
}

/// `path`, or `name (2).ext`, `name (3).ext`, … — the first that doesn't exist.
pub fn unique_path(path: &Path) -> PathBuf {
    if !path.exists() {
        return path.to_path_buf();
    }
    let stem = path.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
    let ext = path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
    (2..)
        .map(|n| path.with_file_name(format!("{stem} ({n}){ext}")))
        .find(|p| !p.exists())
        .expect("some free name")
}

/// Move a source into the `_moved` folder next to it (a rename, so it's
/// instant on the same volume). Returns the new path; a file already in
/// `_moved` stays put.
pub fn park_in_moved(src: &Path) -> Result<PathBuf> {
    let dir = src.parent().ok_or_else(|| Error::Msg(format!("{} has no folder.", src.display())))?;
    if dir.file_name().is_some_and(|n| n == MOVED_DIR) {
        return Ok(src.to_path_buf());
    }
    let moved = dir.join(MOVED_DIR);
    fs::create_dir_all(&moved)?;
    let name = src.file_name().ok_or_else(|| Error::Msg(format!("{} has no name.", src.display())))?;
    let dst = unique_path(&moved.join(name));
    fs::rename(src, &dst)?;
    Ok(dst)
}

/// Can we create a file here? (HandBrake's own "avio_open2 errno" says nothing about why.)
pub fn is_writable_dir(dir: &Path) -> bool {
    if !dir.is_dir() {
        return false;
    }
    let probe = dir.join(format!(".hbs_write_test_{}", std::process::id()));
    let ok = fs::File::create(&probe).is_ok();
    let _ = fs::remove_file(&probe);
    ok
}

/// Free bytes on the volume holding `path`; `None` when it can't be told.
pub fn free_space(path: &Path) -> Option<u64> {
    let mut p = path.to_path_buf();
    while !p.exists() {
        p = p.parent()?.to_path_buf();
    }
    free_space_impl(&p)
}

#[cfg(windows)]
fn free_space_impl(path: &Path) -> Option<u64> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    let mut free: u64 = 0;
    let ok = unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut free, std::ptr::null_mut(), std::ptr::null_mut()) };
    (ok != 0).then_some(free)
}

#[cfg(unix)]
fn free_space_impl(path: &Path) -> Option<u64> {
    use std::os::unix::ffi::OsStrExt;
    let c = std::ffi::CString::new(path.as_os_str().as_bytes()).ok()?;
    let mut st: libc::statvfs = unsafe { std::mem::zeroed() };
    let ok = unsafe { libc::statvfs(c.as_ptr(), &mut st) };
    (ok == 0).then(|| st.f_bavail as u64 * st.f_frsize as u64)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("hbs-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn copies_with_progress() {
        let d = tmp("copy");
        let src = d.join("a.mkv");
        fs::write(&src, b"hello").unwrap();
        let dst = d.join("out/a.mkv");
        let mut last = (0, 0);
        copy_file(&src, &dst, &CancellationToken::new(), |a, b| last = (a, b)).unwrap();
        assert_eq!(fs::read(&dst).unwrap(), b"hello");
        assert_eq!(last, (5, 5));
        assert!(src.exists());
        let cancel = CancellationToken::new();
        cancel.cancel();
        assert!(copy_file(&src, &d.join("c.mkv"), &cancel, |_, _| {}).is_err());
        assert!(!d.join("c.mkv.part").exists());
        fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn parks_in_moved() {
        let d = tmp("park");
        let src = d.join("a.mkv");
        fs::write(&src, b"1").unwrap();
        let moved = park_in_moved(&src).unwrap();
        assert_eq!(moved, d.join("_moved/a.mkv"));
        assert!(!src.exists());
        assert_eq!(park_in_moved(&moved).unwrap(), moved);
        fs::write(&src, b"2").unwrap();
        assert_eq!(park_in_moved(&src).unwrap(), d.join("_moved/a (2).mkv"));
        fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn probes() {
        let d = tmp("probe");
        assert!(is_writable_dir(&d));
        assert!(!is_writable_dir(&d.join("missing")));
        assert!(free_space(&d.join("missing/deeper")).is_some());
        fs::remove_dir_all(&d).unwrap();
    }
}
