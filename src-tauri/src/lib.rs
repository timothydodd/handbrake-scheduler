mod commands;

use std::sync::Arc;

use hbs_core::Job;
use hbs_engine::{Engine, EngineStatus, EventSink, LogLine};
use tauri::{AppHandle, Emitter, Manager, WindowEvent};
use tauri_plugin_notification::NotificationExt;

/// Forwards engine callbacks to the webview as global events.
struct TauriSink(AppHandle);

impl EventSink for TauriSink {
    fn jobs_changed(&self, jobs: &[Job]) {
        let _ = self.0.emit("jobs:changed", jobs);
    }
    fn job_progress(&self, job: &Job) {
        let _ = self.0.emit("job:progress", job);
    }
    fn status_changed(&self, status: &EngineStatus) {
        let _ = self.0.emit("status:changed", status);
    }
    fn log(&self, line: &LogLine) {
        let _ = self.0.emit("log:line", line);
    }
    fn notify(&self, title: &str, body: &str) {
        // Only when the window isn't in front: otherwise the UI shows it already.
        let focused = self.0.get_webview_window("main").and_then(|w| w.is_focused().ok()).unwrap_or(false);
        if !focused {
            let _ = self.0.notification().builder().title(title).body(body).show();
        }
    }
}

pub struct AppState {
    pub engine: Arc<Engine>,
}

fn show_main_window(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.unminimize();
        let _ = w.show();
        let _ = w.set_focus();
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // Must be registered first: a second launch just focuses this instance.
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| show_main_window(app)))
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_notification::init())
        .setup(|app| {
            let data_dir = app.path().app_data_dir()?;
            std::fs::create_dir_all(&data_dir)?;
            let videos = app
                .path()
                .video_dir()
                .or_else(|_| app.path().home_dir().map(|h| h.join("Videos")))
                .unwrap_or_else(|_| data_dir.clone());
            let engine = Engine::new(data_dir, videos, Arc::new(TauriSink(app.handle().clone())));
            tauri::async_runtime::spawn(Arc::clone(&engine).run());
            app.manage(AppState { engine });
            show_main_window(app.handle());
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing mid-encode throws the encode away: let the UI ask first.
            if let WindowEvent::CloseRequested { api, .. } = event {
                let app = window.app_handle();
                if app.state::<AppState>().engine.has_running_job() {
                    api.prevent_close();
                    let _ = app.emit("app:close-requested", ());
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            commands::get_settings,
            commands::save_settings,
            commands::test_handbrake,
            commands::list_presets,
            commands::get_status,
            commands::list_jobs,
            commands::list_received,
            commands::scan_now,
            commands::add_files,
            commands::cancel_job,
            commands::retry_job,
            commands::remove_job,
            commands::clear_finished,
            commands::set_paused,
            commands::open_folder,
            commands::get_log,
            commands::quit_app,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
