//! Tauri commands: thin wrappers over the engine. Every command returns
//! `Result<T, String>` so errors surface as rejected promises in the UI.

use std::path::Path;

use hbs_core::transfer::ReceivedFile;
use hbs_core::{Job, Settings};
use hbs_engine::{EngineStatus, LogLine};
use tauri::{AppHandle, State};
use tauri_plugin_opener::OpenerExt;

use crate::AppState;

type CmdResult<T> = Result<T, String>;

fn err(e: impl std::fmt::Display) -> String {
    e.to_string()
}

// --- settings --------------------------------------------------------------

#[tauri::command]
pub fn get_settings(state: State<'_, AppState>) -> Settings {
    state.engine.settings()
}

#[tauri::command]
pub async fn save_settings(state: State<'_, AppState>, settings: Settings) -> CmdResult<Settings> {
    state.engine.save_settings(settings).await.map_err(err)
}

/// Runs `HandBrakeCLI --version`; resolves to the version line.
#[tauri::command]
pub async fn test_handbrake(state: State<'_, AppState>, path: String) -> CmdResult<String> {
    state.engine.test_handbrake(&path).await.map_err(err)
}

/// `HandBrakeCLI --preset-list`, parsed to names.
#[tauri::command]
pub async fn list_presets(state: State<'_, AppState>, path: String) -> CmdResult<Vec<String>> {
    state.engine.list_presets(&path).await.map_err(err)
}

// --- queue -----------------------------------------------------------------

#[tauri::command]
pub fn get_status(state: State<'_, AppState>) -> EngineStatus {
    state.engine.status()
}

#[tauri::command]
pub fn list_jobs(state: State<'_, AppState>) -> Vec<Job> {
    state.engine.jobs()
}

#[tauri::command]
pub fn list_received(state: State<'_, AppState>) -> Vec<ReceivedFile> {
    state.engine.received()
}

#[tauri::command]
pub fn scan_now(state: State<'_, AppState>) {
    state.engine.scan_now()
}

/// Queue files picked by hand, with the settings of the given watched folder
/// (or the one they're in, or the first).
#[tauri::command]
pub async fn add_files(state: State<'_, AppState>, paths: Vec<String>, folder_index: Option<usize>) -> CmdResult<Vec<Job>> {
    state.engine.add_files(paths, folder_index).await.map_err(err)
}

#[tauri::command]
pub fn cancel_job(state: State<'_, AppState>, id: String) -> CmdResult<()> {
    state.engine.cancel_job(&id).map_err(err)
}

#[tauri::command]
pub fn retry_job(state: State<'_, AppState>, id: String) -> CmdResult<()> {
    state.engine.retry_job(&id).map_err(err)
}

#[tauri::command]
pub fn remove_job(state: State<'_, AppState>, id: String) -> CmdResult<()> {
    state.engine.remove_job(&id).map_err(err)
}

#[tauri::command]
pub fn clear_finished(state: State<'_, AppState>) {
    state.engine.clear_finished()
}

#[tauri::command]
pub fn set_paused(state: State<'_, AppState>, paused: bool) {
    state.engine.set_paused(paused)
}

// --- misc ------------------------------------------------------------------

/// Show a folder in the file manager: only the app's own and configured folders.
#[tauri::command]
pub fn open_folder(app: AppHandle, state: State<'_, AppState>, path: String) -> CmdResult<()> {
    let p = Path::new(&path);
    if !state.engine.is_own_folder(p) || p.components().any(|c| matches!(c, std::path::Component::ParentDir)) {
        return Err("HandbrakeScheduler only opens its own folders.".into());
    }
    let target = if p.is_dir() { p.to_path_buf() } else { p.parent().map(Path::to_path_buf).unwrap_or_default() };
    if !target.exists() {
        std::fs::create_dir_all(&target).map_err(err)?;
    }
    app.opener().open_path(target.to_string_lossy(), None::<&str>).map_err(err)
}

#[tauri::command]
pub fn get_log(state: State<'_, AppState>) -> Vec<LogLine> {
    state.engine.log_lines()
}

/// Quit even though an encode is running (the UI asked first). The encode is
/// stopped and goes back to the queue for next time.
#[tauri::command]
pub async fn quit_app(app: AppHandle, state: State<'_, AppState>) -> CmdResult<()> {
    state.engine.shutdown().await;
    app.exit(0);
    Ok(())
}
