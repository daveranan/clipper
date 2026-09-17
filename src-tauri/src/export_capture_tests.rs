use super::*;

fn ffmpeg_available() -> bool {
    hidden_command("ffmpeg")
        .arg("-version")
        .output()
        .is_ok_and(|output| output.status.success())
}

fn fixture(folder: &Path) -> PathBuf {
    let source = folder.join("source.mp4");
    run_command("ffmpeg", &[
        "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
        "color=red:size=1920x1080:rate=30:duration=1[r];color=blue:size=1920x1080:rate=30:duration=1[b];[r][b]concat=n=2:v=1:a=0",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "libx264", "-preset", "ultrafast",
        "-c:a", "aac", "-shortest", &source.to_string_lossy(),
    ], "Fixture failed").unwrap();
    source
}

fn request(source: &Path, output: &Path) -> ExportRequest {
    let mut settings = AppSettings::default();
    settings.size_cap_enabled = false;
    ExportRequest {
        input_path: source.to_string_lossy().into(),
        output_path: output.to_string_lossy().into(),
        start: 0.0,
        end: 2.0,
        audio_start: 0.0,
        audio_end: 2.0,
        crop: Crop {
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
        },
        output_width: 1920,
        output_height: 1080,
        auto_fit720: false,
        cuts: vec![],
        audio_cuts: vec![],
        settings,
    }
}

#[test]
fn cancellation_is_scoped_to_one_job_and_releases_for_the_next() {
    let jobs = ExportJobs::default();
    let first = jobs.reserve().unwrap();
    assert!(jobs.reserve().is_err());
    assert!(jobs.cancel(&first).unwrap());
    let job = jobs.claim(&first).unwrap();
    assert_eq!(job.control.check().unwrap_err(), export_job::CANCELLED);
    assert!(jobs.claim(&first).is_err());
    drop(job);
    let next = jobs.reserve().unwrap();
    assert!(!jobs.cancel(&first).unwrap());
    let job = jobs.claim(&next).unwrap();
    assert!(job.control.check().is_ok());
}

#[test]
fn stop_terminates_a_running_ffmpeg_and_reaps_it() {
    if !ffmpeg_available() {
        eprintln!("ffmpeg unavailable; skipping process check");
        return;
    }
    let folder = tempfile::tempdir().unwrap();
    let progress = folder.path().join("progress.txt");
    let control = Arc::new(ExportControl::default());
    let worker_control = control.clone();
    let progress_arg = progress.clone();
    let worker = thread::spawn(move || {
        worker_control.run(
            "ffmpeg",
            &[
                "-hide_banner",
                "-loglevel",
                "error",
                "-re",
                "-f",
                "lavfi",
                "-i",
                "color=size=64x64:rate=30",
                "-progress",
                &progress_arg.to_string_lossy(),
                "-t",
                "60",
                "-f",
                "null",
                "-",
            ],
            "Test process failed",
        )
    });
    let deadline = Instant::now() + Duration::from_secs(10);
    while !progress.exists() && !worker.is_finished() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(10));
    }
    let started = progress.exists();
    let stopped = Instant::now();
    control.cancel();
    assert_eq!(worker.join().unwrap().unwrap_err(), export_job::CANCELLED);
    assert!(started, "FFmpeg did not start");
    assert!(stopped.elapsed() < Duration::from_secs(2));
    // FFmpeg's progress handle must have closed before cancellation returns.
    fs::remove_file(progress).unwrap();
}

#[test]
fn screenshots_preserve_source_resolution_time_and_last_frame() {
    if !ffmpeg_available() {
        eprintln!("ffmpeg unavailable; skipping screenshot artifacts");
        return;
    }
    let folder = tempfile::tempdir().unwrap();
    let source = fixture(folder.path());
    let settings = AppSettings::default();
    for (time, red) in [
        (-1.0, true),
        (0.5, true),
        (1.5, false),
        (2.0, false),
        (20.0, false),
    ] {
        let frame = extract_screenshot(&source.to_string_lossy(), time, &settings).unwrap();
        assert_eq!(frame.dimensions(), (1920, 1080));
        let pixel = frame.get_pixel(960, 540).0;
        assert_eq!(pixel[3], 255);
        assert!(
            if red {
                pixel[0] > 240 && pixel[2] < 10
            } else {
                pixel[2] > 240 && pixel[0] < 10
            },
            "Wrong frame at {time}: {pixel:?}"
        );
    }
    assert!(extract_screenshot(&source.to_string_lossy(), f64::NAN, &settings).is_err());
}

#[test]
fn stopped_and_failed_exports_preserve_destination_and_remove_staging() {
    let folder = tempfile::tempdir().unwrap();
    let output = folder.path().join("existing.mp4");
    fs::write(&output, "previous export").unwrap();
    let request = request(&folder.path().join("missing.mp4"), &output);
    let control = ExportControl::default();
    control.cancel();
    assert_eq!(
        export_with_encoder(&request, "x264-medium", &output.to_string_lossy(), &control)
            .unwrap_err(),
        export_job::CANCELLED
    );
    assert!(export_with_encoder(
        &request,
        "x264-medium",
        &output.to_string_lossy(),
        &ExportControl::default()
    )
    .is_err());
    assert_eq!(fs::read_to_string(&output).unwrap(), "previous export");
    assert_eq!(fs::read_dir(folder.path()).unwrap().count(), 1);
}

#[test]
fn video_audio_and_size_capped_exports_publish_only_the_completed_file() {
    if !ffmpeg_available() {
        eprintln!("ffmpeg unavailable; skipping export artifacts");
        return;
    }
    let folder = tempfile::tempdir().unwrap();
    let source = fixture(folder.path());
    for mode in ["copy", "encode", "capped", "audio"] {
        let output = folder.path().join(if mode == "audio" {
            "out.wav"
        } else {
            "out.mp4"
        });
        let mut request = request(&source, &output);
        request.settings.include_video = mode != "audio";
        request.settings.size_cap_enabled = mode == "capped";
        if mode == "encode" || mode == "capped" {
            request.output_width = 640;
            request.output_height = 360;
        }
        let result = export_with_encoder(
            &request,
            "x264-medium",
            &output.to_string_lossy(),
            &ExportControl::default(),
        )
        .unwrap();
        assert_eq!(result.path, output.to_string_lossy());
        assert!(result.bytes > 0);
        if mode == "copy" {
            assert_eq!(fs::read(&source).unwrap(), fs::read(&output).unwrap());
        }
        if mode == "audio" {
            assert_eq!(&fs::read(&output).unwrap()[..4], b"RIFF");
        }
        assert!(!fs::read_dir(folder.path()).unwrap().any(|entry| entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .starts_with(".quickclipper-export-")));
    }
}

#[test]
fn stopping_during_size_capped_encoding_removes_partial_files() {
    if !ffmpeg_available() {
        eprintln!("ffmpeg unavailable; skipping cancellation artifact check");
        return;
    }
    let folder = tempfile::tempdir().unwrap();
    let source = fixture(folder.path());
    let output = folder.path().join("out.mp4");
    fs::write(&output, "previous export").unwrap();
    let mut request = request(&source, &output);
    request.settings.size_cap_enabled = true;
    request.output_width = 3840;
    request.output_height = 2160;
    let control = Arc::new(ExportControl::default());
    let worker_control = control.clone();
    let destination = output.clone();
    let worker = thread::spawn(move || {
        export_with_encoder(
            &request,
            "x264-veryslow",
            &destination.to_string_lossy(),
            &worker_control,
        )
    });
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut encoding_started = false;
    while !worker.is_finished() && Instant::now() < deadline {
        encoding_started = fs::read_dir(folder.path())
            .unwrap()
            .filter_map(Result::ok)
            .filter(|entry| {
                entry
                    .file_name()
                    .to_string_lossy()
                    .starts_with(".quickclipper-export-")
            })
            .any(|entry| fs::read_dir(entry.path()).is_ok_and(|mut files| files.next().is_some()));
        if encoding_started {
            break;
        }
        thread::sleep(Duration::from_millis(5));
    }
    control.cancel();
    assert_eq!(worker.join().unwrap().unwrap_err(), export_job::CANCELLED);
    assert!(encoding_started, "FFmpeg did not begin writing an attempt");
    assert_eq!(fs::read_to_string(output).unwrap(), "previous export");
    assert_eq!(
        fs::read_dir(folder.path()).unwrap().count(),
        2,
        "Partial export files remain"
    );
}
