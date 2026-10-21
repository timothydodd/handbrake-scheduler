//! End-to-end: a watched folder, a fake HandBrakeCLI, one file in → one MP4 out.
#![cfg(unix)]

use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use hbs_core::{FolderSetting, JobStatus, Settings};
use hbs_engine::{Engine, EngineStatus, EventSink, LogLine};

#[derive(Default)]
struct Sink {
    lines: Mutex<Vec<String>>,
}

impl EventSink for Sink {
    fn jobs_changed(&self, _: &[hbs_core::Job]) {}
    fn job_progress(&self, _: &hbs_core::Job) {}
    fn status_changed(&self, _: &EngineStatus) {}
    fn log(&self, line: &LogLine) {
        self.lines.lock().unwrap().push(format!("[{}] {}", line.source, line.text));
    }
    fn notify(&self, _: &str, _: &str) {}
}

const FAKE_CLI: &str = r#"#!/bin/sh
case "$1" in
  --version) echo "HandBrake 1.9.2"; exit 0 ;;
  --preset-list) printf 'General/\n    Fast 1080p30\n        Fast.\n' 1>&2; exit 0 ;;
  --scan) printf '  + duration: 00:10:00\n  + size: 1920x1080, pixel aspect: 1/1, display aspect: 1.78, 23.976 fps\n' 1>&2; exit 0 ;;
esac
# encode: --input X --output Y --preset P --verbose=1
out="$4"
printf 'Encoding: task 1 of 1, 25.00 %% (100.00 fps, avg 90.00 fps, ETA 00h00m03s)\r'
printf 'Encoding: task 1 of 1, 100.00 %% (100.00 fps, avg 90.00 fps, ETA 00h00m00s)\r\n'
printf 'encoded video' > "$out"
exit 0
"#;

fn tmp(name: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("hbs-flow-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

async fn wait_for(engine: &Engine, pred: impl Fn(&[hbs_core::Job]) -> bool) -> Vec<hbs_core::Job> {
    let deadline = Instant::now() + Duration::from_secs(20);
    loop {
        let jobs = engine.jobs();
        if pred(&jobs) {
            return jobs;
        }
        assert!(Instant::now() < deadline, "timed out; jobs: {jobs:?}");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn watched_file_is_encoded_through_staging() {
    let root = tmp("root");
    let cli = root.join("HandBrakeCLI");
    std::fs::write(&cli, FAKE_CLI).unwrap();
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&cli, std::fs::Permissions::from_mode(0o755)).unwrap();
    let (input, output, staging, data) = (root.join("in"), root.join("out"), root.join("stage"), root.join("data"));
    std::fs::create_dir_all(input.join("Movies")).unwrap();
    std::fs::write(input.join("Movies/Film (2001).mkv"), b"source bytes").unwrap();
    // Make the file old enough to count as finished writing.
    let old = std::time::SystemTime::now() - Duration::from_secs(120);
    std::fs::File::open(input.join("Movies/Film (2001).mkv")).unwrap().set_modified(old).unwrap();

    let mut settings = Settings {
        handbrake_path: cli.to_string_lossy().to_string(),
        server_enabled: false,
        folders: vec![FolderSetting {
            input_path: input.to_string_lossy().to_string(),
            output_path: output.to_string_lossy().to_string(),
            preset: "Fast 1080p30".into(),
            staging_path: staging.to_string_lossy().to_string(),
            delete_source: true,
            ..Default::default()
        }],
        ..Default::default()
    };
    settings.normalize(&root);
    std::fs::create_dir_all(&data).unwrap();
    std::fs::write(data.join("settings.json"), serde_json::to_vec(&settings).unwrap()).unwrap();

    let sink = Arc::new(Sink::default());
    let engine = Engine::new(data.clone(), root.clone(), sink.clone());
    assert_eq!(engine.test_handbrake(&settings.handbrake_path).await.unwrap(), "HandBrake 1.9.2");
    assert_eq!(engine.list_presets(&settings.handbrake_path).await.unwrap(), vec!["Fast 1080p30"]);

    tokio::spawn(Arc::clone(&engine).run());
    let jobs = wait_for(&engine, |j| j.iter().any(|j| j.status == JobStatus::Completed)).await;
    let job = &jobs[0];
    assert_eq!(job.preset, "Fast 1080p30");
    assert_eq!((job.width, job.height), (Some(1920), Some(1080)));
    assert_eq!(job.output_file.as_deref(), Some(output.join("Movies/Film (2001).mp4").to_str().unwrap()));
    assert_eq!(std::fs::read(output.join("Movies/Film (2001).mp4")).unwrap(), b"encoded video");
    assert!(job.input_path.contains("_moved"), "{}", job.input_path);
    assert!(!PathBuf::from(&job.input_path).exists(), "source deleted after success");
    assert!(std::fs::read_dir(&staging).unwrap().next().is_none(), "staging cleaned up");

    // A second scan doesn't queue it again (it's in _moved and already done).
    engine.scan_now();
    tokio::time::sleep(Duration::from_millis(500)).await;
    assert_eq!(engine.jobs().len(), 1);

    // Retry refuses: the source is gone.
    assert!(engine.retry_job(&job.id).is_err());
    engine.clear_finished();
    assert!(engine.jobs().is_empty());

    let lines = sink.lines.lock().unwrap().join("\n");
    assert!(lines.contains("Finished Film (2001).mkv"), "{lines}");
    engine.shutdown().await;
    let _ = std::fs::remove_dir_all(&root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn failing_encode_is_retried_then_failed() {
    let root = tmp("fail");
    let cli = root.join("HandBrakeCLI");
    std::fs::write(&cli, "#!/bin/sh\ncase \"$1\" in --version) echo HandBrake; exit 0;; --scan) exit 0;; esac\necho 'ERROR: avio_open2 failed' 1>&2\nexit 3\n").unwrap();
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&cli, std::fs::Permissions::from_mode(0o755)).unwrap();
    let (input, output, data) = (root.join("in"), root.join("out"), root.join("data"));
    std::fs::create_dir_all(&input).unwrap();
    std::fs::write(input.join("a.mkv"), b"x").unwrap();
    let old = std::time::SystemTime::now() - Duration::from_secs(120);
    std::fs::File::open(input.join("a.mkv")).unwrap().set_modified(old).unwrap();
    let mut settings = Settings {
        handbrake_path: cli.to_string_lossy().to_string(),
        server_enabled: false,
        max_attempts: 2,
        folders: vec![FolderSetting {
            input_path: input.to_string_lossy().to_string(),
            output_path: output.to_string_lossy().to_string(),
            use_staging: false,
            ..Default::default()
        }],
        ..Default::default()
    };
    settings.normalize(&root);
    std::fs::create_dir_all(&data).unwrap();
    std::fs::write(data.join("settings.json"), serde_json::to_vec(&settings).unwrap()).unwrap();
    let engine = Engine::new(data, root.clone(), Arc::new(Sink::default()));
    tokio::spawn(Arc::clone(&engine).run());
    let jobs = wait_for(&engine, |j| j.iter().any(|j| j.status == JobStatus::Failed)).await;
    assert_eq!(jobs[0].attempts, 2);
    assert!(jobs[0].error.as_deref().unwrap().contains("exit code 3"), "{:?}", jobs[0].error);
    assert!(jobs[0].error.as_deref().unwrap().contains("avio_open2"));
    assert!(!output.join("a.mp4").exists());
    // The source is parked, so a retry is possible once the problem is fixed.
    assert!(PathBuf::from(&jobs[0].input_path).exists());
    engine.retry_job(&jobs[0].id).unwrap();
    assert_eq!(engine.jobs()[0].status, JobStatus::Pending);
    engine.shutdown().await;
    let _ = std::fs::remove_dir_all(&root);
}
