//! Reading what HandBrakeCLI prints: encode progress lines, `--scan` output
//! (source resolution) and `--preset-list`.

use serde::{Deserialize, Serialize};

/// One `Encoding: task 1 of 1, 12.34 % (45.67 fps, avg 50.00 fps, ETA 00h12m34s)` line.
#[derive(Debug, Clone, Copy, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Progress {
    pub percent: f64,
    pub fps: Option<f64>,
    pub avg_fps: Option<f64>,
    pub eta_secs: Option<u64>,
}

/// Parse a progress line; `None` for anything else HandBrake prints.
pub fn parse_progress(line: &str) -> Option<Progress> {
    let rest = line.split("Encoding:").nth(1)?;
    let (before, after) = rest.split_once('%')?;
    let percent: f64 = before.rsplit(',').next()?.trim().parse().ok()?;
    let mut p = Progress { percent, ..Default::default() };
    if let Some(inner) = after.trim().strip_prefix('(').and_then(|s| s.strip_suffix(')')) {
        for part in inner.split(',').map(str::trim) {
            if let Some(v) = part.strip_prefix("avg ").and_then(|s| s.strip_suffix(" fps")) {
                p.avg_fps = v.trim().parse().ok();
            } else if let Some(v) = part.strip_suffix(" fps") {
                p.fps = v.trim().parse().ok();
            } else if let Some(v) = part.strip_prefix("ETA ") {
                p.eta_secs = parse_eta(v.trim());
            }
        }
    }
    Some(p)
}

/// `00h12m34s` → seconds.
fn parse_eta(s: &str) -> Option<u64> {
    let (h, rest) = s.split_once('h')?;
    let (m, rest) = rest.split_once('m')?;
    let sec = rest.strip_suffix('s')?;
    Some(h.parse::<u64>().ok()? * 3600 + m.parse::<u64>().ok()? * 60 + sec.parse::<u64>().ok()?)
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VideoInfo {
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub duration_secs: Option<u64>,
    pub fps: Option<f64>,
}

impl VideoInfo {
    /// "4K", "1080p", … from the height.
    pub fn category(&self) -> &'static str {
        match self.height {
            Some(h) if h >= 2160 => "4K",
            Some(h) if h >= 1440 => "1440p",
            Some(h) if h >= 1080 => "1080p",
            Some(h) if h >= 720 => "720p",
            Some(h) if h >= 480 => "480p",
            Some(_) => "SD",
            None => "unknown",
        }
    }
}

/// Pull the first title's size, duration and frame rate out of `--scan` output
/// (`  + size: 1920x1080, pixel aspect: 1/1, display aspect: 1.78, 23.976 fps`,
/// `  + duration: 01:52:03`).
pub fn parse_scan(output: &str) -> VideoInfo {
    let mut info = VideoInfo::default();
    for line in output.lines() {
        let t = line.trim();
        let Some(body) = t.strip_prefix('+').map(str::trim) else { continue };
        if info.width.is_none() {
            if let Some(v) = body.strip_prefix("size:").or_else(|| body.strip_prefix("dimensions:")) {
                let dims = v.split(',').next().unwrap_or("").trim();
                if let Some((w, h)) = dims.split_once('x') {
                    info.width = w.trim().parse().ok();
                    info.height = h.trim().parse().ok();
                }
                if info.fps.is_none() {
                    info.fps = v.split(',').map(str::trim).find_map(|p| p.strip_suffix(" fps")).and_then(|f| f.trim().parse().ok());
                }
                continue;
            }
        }
        if info.duration_secs.is_none() {
            if let Some(v) = body.strip_prefix("duration:") {
                let mut it = v.trim().split(':').map(|x| x.trim().parse::<u64>().ok());
                if let (Some(Some(h)), Some(Some(m)), Some(Some(s))) = (it.next(), it.next(), it.next()) {
                    info.duration_secs = Some(h * 3600 + m * 60 + s);
                }
                continue;
            }
        }
        if info.fps.is_none() {
            if let Some(v) = body.strip_prefix("rate:") {
                info.fps = v.trim().strip_suffix("fps").and_then(|f| f.trim().parse().ok());
            }
        }
    }
    info
}

/// Preset names from `--preset-list` (category lines end with `/`, presets are
/// indented under them; the lines after a preset are its description).
pub fn parse_preset_list(output: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut in_category = false;
    for line in output.lines() {
        let trimmed = line.trim_end();
        if trimmed.is_empty() {
            continue;
        }
        let indent = trimmed.len() - trimmed.trim_start().len();
        let text = trimmed.trim();
        if indent == 0 && text.ends_with('/') {
            in_category = true;
            continue;
        }
        if in_category && indent == 4 && !out.contains(&text.to_string()) {
            out.push(text.to_string());
        }
    }
    out
}

/// Whether a HandBrake stderr line looks like a real problem (shown as an error in the log).
pub fn is_error_line(line: &str) -> bool {
    let l = line.to_ascii_lowercase();
    ["error:", "failed", "cannot", "unable to", "not found", "invalid", "corrupted", "errno"].iter().any(|k| l.contains(k))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_lines() {
        let p = parse_progress("Encoding: task 1 of 1, 12.34 % (45.67 fps, avg 50.00 fps, ETA 00h12m34s)").unwrap();
        assert_eq!(p.percent, 12.34);
        assert_eq!(p.fps, Some(45.67));
        assert_eq!(p.avg_fps, Some(50.0));
        assert_eq!(p.eta_secs, Some(754));
        let p = parse_progress("\rEncoding: task 2 of 2, 0.50 %").unwrap();
        assert_eq!(p.percent, 0.5);
        assert_eq!(p.fps, None);
        assert!(parse_progress("[12:00:00] sync: first pts").is_none());
    }

    #[test]
    fn scan_output() {
        let out = "\
[00:00:01] scan: 1 title(s) found
+ title 1:
  + stream: a.mkv
  + duration: 01:52:03
  + size: 3840x2160, pixel aspect: 1/1, display aspect: 1.78, 23.976 fps
  + autocrop: 0/0/0/0
  + chapters:
    + 1: duration 00:05:00
";
        let v = parse_scan(out);
        assert_eq!((v.width, v.height), (Some(3840), Some(2160)));
        assert_eq!(v.duration_secs, Some(6723));
        assert_eq!(v.fps, Some(23.976));
        assert_eq!(v.category(), "4K");
        let old = parse_scan("  + size: 720x576, pixel aspect: 16/15\n  + rate: 25.000 fps\n");
        assert_eq!(old.height, Some(576));
        assert_eq!(old.fps, Some(25.0));
        assert_eq!(old.category(), "480p");
        assert_eq!(parse_scan("nothing").category(), "unknown");
    }

    #[test]
    fn preset_list() {
        let out = "\
General/
    Very Fast 1080p30
        Small H.264 video (up to 1080p30) and AAC stereo audio, in an MP4 container.
    HQ 1080p30 Surround
        High quality H.264 video.
Devices/
    Android 1080p30
";
        assert_eq!(parse_preset_list(out), vec!["Very Fast 1080p30", "HQ 1080p30 Surround", "Android 1080p30"]);
    }

    #[test]
    fn error_lines() {
        assert!(is_error_line("ERROR: avio_open2 failed errno -2"));
        assert!(!is_error_line("[12:00:00] sync: first pts"));
    }
}
