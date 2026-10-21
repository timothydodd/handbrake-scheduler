//! Running HandBrakeCLI and streaming its output line by line.
//!
//! HandBrake writes progress with carriage returns (no newline until the
//! encode ends), so lines are split on `\r` as well as `\n`.

use std::path::Path;
use std::process::Stdio;
use std::time::{Duration, Instant};

use tokio::io::AsyncReadExt;
use tokio::process::Command;
use tokio_util::sync::CancellationToken;

use crate::{Error, Result};

pub enum Outcome {
    Finished { code: Option<i32> },
    Cancelled,
    /// Nothing printed for the stall timeout; the process was killed.
    Stalled,
}

/// A command for a console tool that must never flash a console window on Windows.
pub fn quiet_command(program: impl AsRef<std::ffi::OsStr>) -> Command {
    #[allow(unused_mut)]
    let mut cmd = Command::new(program);
    #[cfg(windows)]
    {
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    cmd
}

fn check_exe(exe: &str) -> Result<()> {
    if exe.trim().is_empty() {
        return Err(Error::Msg("No HandBrakeCLI path is set. Check Settings.".into()));
    }
    if exe.contains(['/', '\\']) && !Path::new(exe).exists() {
        return Err(Error::Msg(format!("HandBrakeCLI wasn't found at {exe}. Check the path in Settings.")));
    }
    Ok(())
}

/// Run HandBrakeCLI with `args`, calling `on_line` for every stdout/stderr
/// line (split on `\r` or `\n`). Killed on cancel, or when nothing has been
/// printed for `stall`.
pub async fn run_handbrake(
    exe: &str,
    args: &[String],
    stall: Duration,
    cancel: &CancellationToken,
    mut on_line: impl FnMut(&str),
) -> Result<Outcome> {
    check_exe(exe)?;
    let mut child = quiet_command(exe)
        .args(args)
        .env("LANG", "en_US.UTF-8")
        .env("LC_ALL", "en_US.UTF-8")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
        .map_err(|e| Error::Msg(format!("Couldn't start HandBrakeCLI ({exe}): {e}")))?;
    #[cfg(windows)]
    job::attach(&child);

    let mut out = child.stdout.take().expect("piped stdout");
    let mut err = child.stderr.take().expect("piped stderr");
    let mut out_split = LineSplitter::default();
    let mut err_split = LineSplitter::default();
    let (mut out_buf, mut err_buf) = ([0u8; 8192], [0u8; 8192]);
    let (mut out_open, mut err_open) = (true, true);
    let mut last_activity = Instant::now();
    let mut tick = tokio::time::interval(Duration::from_secs(5));
    tick.tick().await;

    while out_open || err_open {
        tokio::select! {
            _ = cancel.cancelled() => {
                let _ = child.kill().await;
                return Ok(Outcome::Cancelled);
            }
            _ = tick.tick() => {
                if last_activity.elapsed() >= stall {
                    let _ = child.kill().await;
                    return Ok(Outcome::Stalled);
                }
            }
            n = out.read(&mut out_buf), if out_open => match n {
                Ok(0) | Err(_) => {
                    out_open = false;
                    out_split.finish(&mut on_line);
                }
                Ok(n) => {
                    last_activity = Instant::now();
                    out_split.push(&out_buf[..n], &mut on_line);
                }
            },
            n = err.read(&mut err_buf), if err_open => match n {
                Ok(0) | Err(_) => {
                    err_open = false;
                    err_split.finish(&mut on_line);
                }
                Ok(n) => {
                    last_activity = Instant::now();
                    err_split.push(&err_buf[..n], &mut on_line);
                }
            },
        }
    }

    tokio::select! {
        _ = cancel.cancelled() => {
            let _ = child.kill().await;
            Ok(Outcome::Cancelled)
        }
        status = child.wait() => Ok(Outcome::Finished { code: status.map_err(Error::Io)?.code() }),
    }
}

/// Run HandBrakeCLI to completion and return everything it printed (both
/// streams, in order received). Killed after `timeout`.
pub async fn capture(exe: &str, args: &[String], timeout: Duration) -> Result<(Option<i32>, String)> {
    let mut text = String::new();
    let cancel = CancellationToken::new();
    let run = run_handbrake(exe, args, timeout, &cancel, |l| {
        text.push_str(l);
        text.push('\n');
    });
    let outcome = tokio::time::timeout(timeout, run).await;
    match outcome {
        Ok(Ok(Outcome::Finished { code })) => Ok((code, text)),
        Ok(Ok(Outcome::Stalled)) | Err(_) => Err(Error::Msg(format!("HandBrakeCLI didn't finish within {} seconds.", timeout.as_secs()))),
        Ok(Ok(Outcome::Cancelled)) => Err(Error::Msg("Cancelled.".into())),
        Ok(Err(e)) => Err(e),
    }
}

/// Splits a byte stream into lines on `\r`, `\n` or `\r\n`.
#[derive(Default)]
struct LineSplitter {
    pending: Vec<u8>,
    last_was_cr: bool,
}

impl LineSplitter {
    fn push(&mut self, bytes: &[u8], on_line: &mut impl FnMut(&str)) {
        for &b in bytes {
            match b {
                b'\n' if self.last_was_cr => self.last_was_cr = false,
                b'\n' | b'\r' => {
                    self.last_was_cr = b == b'\r';
                    self.flush(on_line);
                }
                _ => {
                    self.last_was_cr = false;
                    self.pending.push(b);
                }
            }
        }
    }

    fn finish(&mut self, on_line: &mut impl FnMut(&str)) {
        if !self.pending.is_empty() {
            self.flush(on_line);
        }
    }

    fn flush(&mut self, on_line: &mut impl FnMut(&str)) {
        let line = String::from_utf8_lossy(&self.pending).to_string();
        self.pending.clear();
        let t = line.trim_end();
        if !t.is_empty() {
            on_line(t);
        }
    }
}

/// Windows: a job object that kills every HandBrakeCLI we spawn when this
/// process ends, however it ends (quit, crash, Task Manager).
#[cfg(windows)]
mod job {
    use std::sync::OnceLock;
    use windows_sys::Win32::Foundation::HANDLE;
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };

    struct Job(HANDLE);
    unsafe impl Send for Job {}
    unsafe impl Sync for Job {}

    static JOB: OnceLock<Option<Job>> = OnceLock::new();

    fn job() -> Option<HANDLE> {
        JOB.get_or_init(|| unsafe {
            let h = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if h.is_null() {
                return None;
            }
            let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            let ok = SetInformationJobObject(
                h,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const std::ffi::c_void,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            (ok != 0).then_some(Job(h))
        })
        .as_ref()
        .map(|j| j.0)
    }

    pub fn attach(child: &tokio::process::Child) {
        if let (Some(job), Some(h)) = (job(), child.raw_handle()) {
            unsafe { AssignProcessToJobObject(job, h as HANDLE) };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_on_cr_and_lf() {
        let mut lines = Vec::new();
        let mut s = LineSplitter::default();
        s.push(b"a\rb\r\nc\nd", &mut |l| lines.push(l.to_string()));
        s.push(b"e\r", &mut |l| lines.push(l.to_string()));
        s.finish(&mut |l| lines.push(l.to_string()));
        assert_eq!(lines, vec!["a", "b", "c", "de"]);
    }

    #[tokio::test]
    async fn missing_exe_is_a_readable_error() {
        let e = run_handbrake("/no/such/HandBrakeCLI", &[], Duration::from_secs(1), &CancellationToken::new(), |_| {}).await.err().unwrap();
        assert!(e.to_string().contains("wasn't found"));
    }
}
