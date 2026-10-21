//! The HTTP upload server AutoMk talks to. Same routes and shapes as the old
//! .NET scheduler: `POST /upload` (multipart `metadata` + `file`), `GET /files`,
//! `GET /jobs`, `POST /stop`, `GET /health`.

use std::path::PathBuf;
use std::sync::Arc;

use axum::extract::{DefaultBodyLimit, Multipart, State};
use axum::http::StatusCode;
use axum::routing::{get, post};
use axum::{Json, Router};
use hbs_core::transfer::{iso_utc, ReceivedFile, TransferRequest};
use hbs_core::now_ms;
use serde_json::{json, Value};
use tokio::io::{AsyncWriteExt, BufWriter};
use tokio_util::sync::CancellationToken;

use crate::engine::Engine;
use crate::{Error, Result};

pub struct ServerHandle {
    shutdown: CancellationToken,
    task: tokio::task::JoinHandle<()>,
}

impl ServerHandle {
    pub async fn stop(self) {
        self.shutdown.cancel();
        let _ = tokio::time::timeout(std::time::Duration::from_secs(5), self.task).await;
    }
}

type App = Arc<Engine>;

/// Bind `address` and serve until the handle is stopped.
pub async fn start(engine: Arc<Engine>, address: &str, max_upload_bytes: u64) -> Result<ServerHandle> {
    let listener = tokio::net::TcpListener::bind(address)
        .await
        .map_err(|e| Error::Msg(format!("Couldn't listen on {address}: {e}")))?;
    let limit = usize::try_from(max_upload_bytes).unwrap_or(usize::MAX).saturating_add(16 << 20);
    let app = Router::new()
        .route("/health", get(health))
        .route("/files", get(files))
        .route("/jobs", get(jobs))
        .route("/stop", post(stop))
        .route("/upload", post(upload))
        .layer(DefaultBodyLimit::max(limit))
        .with_state(engine);
    let shutdown = CancellationToken::new();
    let token = shutdown.clone();
    let task = tokio::spawn(async move {
        let _ = axum::serve(listener, app).with_graceful_shutdown(token.cancelled_owned()).await;
    });
    Ok(ServerHandle { shutdown, task })
}

async fn health() -> &'static str {
    "Healthy"
}

async fn files(State(engine): State<App>) -> Json<Vec<ReceivedFile>> {
    Json(engine.received())
}

async fn jobs(State(engine): State<App>) -> Json<Value> {
    let jobs = engine.jobs();
    let pending = jobs.iter().filter(|j| j.status == hbs_core::JobStatus::Pending).count();
    let processing = jobs.iter().any(|j| j.status == hbs_core::JobStatus::Processing);
    Json(json!({
        "QueueCount": pending,
        "IsProcessing": processing,
        "IsEmpty": pending == 0 && !processing,
        "Status": if processing { "Processing" } else if pending == 0 { "Idle" } else { "Queued" },
    }))
}

async fn stop(State(engine): State<App>) -> Json<Value> {
    engine.stop_current("the /stop endpoint");
    Json(json!({ "Message": "Processing stopped" }))
}

enum UploadError {
    Bad(String),
    Internal(String),
}

async fn upload(State(engine): State<App>, multipart: Multipart) -> (StatusCode, Json<Value>) {
    match receive(&engine, multipart).await {
        Ok(received) => {
            let name = received.original_file_name.clone();
            let path = received.file_path.clone();
            engine.queue_upload(received);
            (
                StatusCode::OK,
                Json(json!({ "Message": "File received successfully", "FileName": name, "FilePath": path, "ProcessingQueued": true })),
            )
        }
        Err(UploadError::Bad(e)) => (StatusCode::BAD_REQUEST, Json(json!({ "Error": e }))),
        Err(UploadError::Internal(e)) => (StatusCode::INTERNAL_SERVER_ERROR, Json(json!({ "Error": e }))),
    }
}

/// Stream the file part to `<incoming>/upload-<n>.part`, then rename it into
/// place once every part has been read, so a scan never sees a half file.
async fn receive(engine: &Engine, mut multipart: Multipart) -> std::result::Result<ReceivedFile, UploadError> {
    let settings = engine.settings();
    let incoming = PathBuf::from(&settings.incoming_dir);
    tokio::fs::create_dir_all(&incoming).await.map_err(|e| UploadError::Internal(format!("Couldn't create the incoming folder: {e}")))?;

    let mut request: Option<TransferRequest> = None;
    let mut part_path: Option<PathBuf> = None;
    let mut bytes_written = 0u64;

    let result: std::result::Result<(), UploadError> = async {
        while let Some(mut field) = multipart.next_field().await.map_err(|e| UploadError::Bad(format!("Bad multipart body: {e}")))? {
            let name = field.name().unwrap_or("").to_string();
            if name.eq_ignore_ascii_case("metadata") && field.file_name().is_none() {
                let text = field.text().await.map_err(|e| UploadError::Bad(format!("Couldn't read metadata: {e}")))?;
                request = Some(serde_json::from_str(&text).map_err(|_| UploadError::Bad("Invalid metadata format".into()))?);
            } else if field.file_name().is_some() || name.eq_ignore_ascii_case("file") {
                if part_path.is_some() {
                    return Err(UploadError::Bad("Multiple file parts are not supported".into()));
                }
                let path = incoming.join(format!("upload-{}-{}.part", now_ms(), std::process::id()));
                let file = tokio::fs::File::create(&path).await.map_err(|e| UploadError::Internal(format!("Couldn't create the upload file: {e}")))?;
                part_path = Some(path);
                let mut w = BufWriter::with_capacity(1 << 20, file);
                while let Some(chunk) = field.chunk().await.map_err(|e| UploadError::Bad(format!("Upload interrupted: {e}")))? {
                    bytes_written += chunk.len() as u64;
                    if bytes_written > settings.max_upload_bytes {
                        return Err(UploadError::Bad(format!("File exceeds maximum size {}", settings.max_upload_bytes)));
                    }
                    w.write_all(&chunk).await.map_err(|e| UploadError::Internal(format!("Couldn't write the upload: {e}")))?;
                }
                w.flush().await.map_err(|e| UploadError::Internal(format!("Couldn't write the upload: {e}")))?;
                w.into_inner().sync_all().await.map_err(|e| UploadError::Internal(format!("Couldn't write the upload: {e}")))?;
            }
        }
        Ok(())
    }
    .await;

    let finish = async {
        result?;
        let request = request.ok_or_else(|| UploadError::Bad("Missing metadata".into()))?;
        let part = part_path.clone().ok_or_else(|| UploadError::Bad("No file uploaded".into()))?;
        let relative = request.stored_relative();
        let final_path = incoming.join(&relative);
        if let Some(parent) = final_path.parent() {
            tokio::fs::create_dir_all(parent).await.map_err(|e| UploadError::Internal(format!("Couldn't create the target folder: {e}")))?;
        }
        if tokio::fs::metadata(&final_path).await.is_ok() {
            tokio::fs::remove_file(&final_path).await.map_err(|e| UploadError::Internal(format!("Couldn't replace the existing file: {e}")))?;
        }
        tokio::fs::rename(&part, &final_path).await.map_err(|e| UploadError::Internal(format!("Couldn't move the upload into place: {e}")))?;
        part_path = None;
        Ok(ReceivedFile {
            original_file_name: request.file_name(),
            stored_file_name: relative.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default(),
            file_path: final_path.to_string_lossy().to_string(),
            file_size_bytes: bytes_written,
            received_timestamp: iso_utc(now_ms()),
            relative_file_path: request.relative_file_path.clone().unwrap_or_default(),
            transfer_request: request,
        })
    }
    .await;

    if let Some(part) = part_path {
        let _ = tokio::fs::remove_file(part).await;
    }
    if let Err(e) = &finish {
        let what = match e {
            UploadError::Bad(m) | UploadError::Internal(m) => m.clone(),
        };
        engine.log("server", &format!("Upload refused: {what}"), true);
    }
    finish
}
