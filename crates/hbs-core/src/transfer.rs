//! The upload contract shared with AutoMk (unchanged from the .NET scheduler):
//! `POST /upload` as multipart with a `metadata` part (JSON, .NET PascalCase
//! names) and a `file` part. AutoMk sends `OriginalFileName`, `FileSizeBytes`,
//! `RelativeFilePath` and `TransferTimestamp`; the old richer `MediaInfo` is
//! still accepted.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TransferRequest {
    pub original_file_name: String,
    pub file_size_bytes: u64,
    pub media_info: MediaInfo,
    pub transfer_timestamp: String,
    pub relative_file_path: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct MediaInfo {
    pub series_title: String,
    pub movie_title: String,
    pub season: u32,
    pub episode: u32,
    pub year: Option<String>,
    pub media_type: String,
    pub episode_title: Option<String>,
    pub imdb_id: Option<String>,
    pub original_disc_name: String,
    pub genre: Option<String>,
    pub runtime: Option<String>,
    pub additional_metadata: std::collections::BTreeMap<String, String>,
}

impl TransferRequest {
    /// The file name only (clients may send a path).
    pub fn file_name(&self) -> String {
        self.original_file_name
            .rsplit(['/', '\\'])
            .next()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or("upload.mkv")
            .to_string()
    }

    /// The path under the incoming folder the file is stored at: the relative
    /// path when a safe one was sent, else just the file name.
    pub fn stored_relative(&self) -> std::path::PathBuf {
        self.relative_file_path
            .as_deref()
            .and_then(crate::paths::safe_relative)
            .filter(|p| p.file_name().is_some())
            .unwrap_or_else(|| std::path::PathBuf::from(self.file_name()))
    }
}

/// A file the server received (the `GET /files` listing).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ReceivedFile {
    pub original_file_name: String,
    pub stored_file_name: String,
    pub file_path: String,
    pub file_size_bytes: u64,
    /// ISO 8601 UTC.
    pub received_timestamp: String,
    pub relative_file_path: String,
    pub transfer_request: TransferRequest,
}

/// Milliseconds since the epoch → ISO 8601 UTC, the way .NET prints it.
pub fn iso_utc(ms: i64) -> String {
    let secs = ms.div_euclid(1000);
    let frac = ms.rem_euclid(1000);
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}.{frac:03}0000Z", rem / 3600, (rem / 60) % 60, rem % 60)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn reads_automk_metadata() {
        let r: TransferRequest = serde_json::from_str(
            r#"{"OriginalFileName":"A (2000).mkv","FileSizeBytes":5,"RelativeFilePath":"Movies\\A (2000)\\A (2000).mkv","TransferTimestamp":"2026-10-12T22:04:05.0000000Z"}"#,
        )
        .unwrap();
        assert_eq!(r.file_name(), "A (2000).mkv");
        assert_eq!(r.stored_relative(), PathBuf::from("Movies/A (2000)/A (2000).mkv"));
        assert_eq!(r.media_info.media_type, "");
    }

    #[test]
    fn reads_old_rich_metadata_and_falls_back() {
        let r: TransferRequest = serde_json::from_str(
            r#"{"OriginalFileName":"C:\\x\\video.mkv","FileSizeBytes":1,"MediaInfo":{"MediaType":"Movie","MovieTitle":"Example","AdditionalMetadata":{"a":"b"}},"TransferTimestamp":"2024-01-01T00:00:00Z"}"#,
        )
        .unwrap();
        assert_eq!(r.media_info.movie_title, "Example");
        assert_eq!(r.stored_relative(), PathBuf::from("video.mkv"));
        let bad = TransferRequest { relative_file_path: Some("../x.mkv".into()), original_file_name: "x.mkv".into(), ..Default::default() };
        assert_eq!(bad.stored_relative(), PathBuf::from("x.mkv"));
    }

    #[test]
    fn iso_dates() {
        assert_eq!(iso_utc(0), "1970-01-01T00:00:00.0000000Z");
        assert_eq!(iso_utc(951_782_400_123), "2000-02-29T00:00:00.1230000Z");
    }
}
