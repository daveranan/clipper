use super::*;

fn export_request(output_width: u32, output_height: u32, auto_fit720: bool) -> ExportRequest {
    let mut settings = AppSettings::default();
    settings.include_audio = false;
    settings.size_cap_enabled = false;
    ExportRequest {
        input_path: "input.mp4".to_string(),
        output_path: "output.mp4".to_string(),
        start: 0.0,
        end: 1.0,
        audio_start: 0.0,
        audio_end: 1.0,
        crop: Crop { x: 0, y: 0, width: 640, height: 360 },
        output_width,
        output_height,
        auto_fit720,
        cuts: Vec::new(),
        audio_cuts: Vec::new(),
        settings,
    }
}

#[test]
fn export_crop_matches_even_inspector_coordinates() {
    let crop = clamp_export_crop(&Crop { x: 101, y: 53, width: 801, height: 451 }, 1920, 1080);
    assert_eq!((crop.x, crop.y, crop.width, crop.height), (100, 52, 800, 450));
}

#[test]
fn manual_dimensions_are_not_ignored_when_auto_fit_is_off() {
    let manual = export_request(854, 480, false);
    assert_eq!(build_filters(&manual, &manual.crop, 640, 360), "scale=854:480");

    let automatic = export_request(854, 480, true);
    assert_eq!(
        build_filters(&automatic, &automatic.crop, 640, 360),
        "crop=min(iw\\,ih*16/9):min(ih\\,iw*9/16):(iw-ow)/2:(ih-oh)/2,scale=1280:720",
    );
}

#[test]
fn ffmpeg_filters_produce_the_requested_output_dimensions() {
    let available = Command::new("ffmpeg").arg("-version").output();
    if !available.is_ok_and(|output| output.status.success()) {
        eprintln!("ffmpeg unavailable; skipping artifact dimension check");
        return;
    }

    for (request, expected) in [
        (export_request(854, 480, false), (854, 480)),
        (export_request(854, 480, true), (1280, 720)),
    ] {
        let output_path = std::env::temp_dir().join(format!("quickclipper-dimension-{}.mp4", Uuid::new_v4()));
        let filter = build_filters(&request, &request.crop, 640, 360);
        let result = Command::new("ffmpeg")
            .args(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=size=640x360:rate=1", "-frames:v", "1", "-vf"])
            .arg(filter)
            .args(["-pix_fmt", "yuv420p"])
            .arg(&output_path)
            .output()
            .expect("run ffmpeg dimension fixture");
        assert!(result.status.success(), "ffmpeg failed: {}", String::from_utf8_lossy(&result.stderr));

        let probe = Command::new("ffprobe")
            .args(["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x"])
            .arg(&output_path)
            .output()
            .expect("probe exported dimensions");
        assert!(probe.status.success(), "ffprobe failed: {}", String::from_utf8_lossy(&probe.stderr));
        assert_eq!(String::from_utf8_lossy(&probe.stdout).trim(), format!("{}x{}", expected.0, expected.1));
        let _ = fs::remove_file(&output_path);
    }
}
