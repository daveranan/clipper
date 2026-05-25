use chrono::Local;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
#[cfg(windows)]
use std::os::windows::process::CommandExt;
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Emitter, Manager, State, WindowEvent,
};
use uuid::Uuid;

struct AppState {
    recording: Mutex<Option<ActiveRecording>>,
}

struct ActiveRecording {
    child: Child,
    overlay_child: Option<Child>,
    audio: Option<LoopbackRecording>,
    path: PathBuf,
}

struct LoopbackRecording {
    stop: Arc<AtomicBool>,
    thread: thread::JoinHandle<Result<Option<PathBuf>, String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[serde(default)]
pub struct AppSettings {
    save_folder: String,
    ffmpeg_path: String,
    frame_rate: u32,
    max_megabytes: f64,
    quality_length_cap_enabled: bool,
    quality_target_kbps: u32,
    export_encoder_key: String,
    export_bitrate_scale: f64,
    audio_gain_db: f64,
    unsupported_encoder_keys: Vec<String>,
    encoder_benchmarks: Vec<EncoderBenchmark>,
    include_audio: bool,
    audio_device_name: String,
    start_with_windows: bool,
    record_hotkey: String,
    reset_hotkey: String,
    github_repository_url: String,
}

impl Default for AppSettings {
    fn default() -> Self {
        let save_folder = dirs::video_dir()
            .unwrap_or_else(|| dirs::home_dir().unwrap_or_else(|| PathBuf::from(".")))
            .join("QuickClipper")
            .to_string_lossy()
            .to_string();

        Self {
            save_folder,
            ffmpeg_path: "ffmpeg".to_string(),
            frame_rate: 30,
            max_megabytes: 9.8,
            quality_length_cap_enabled: true,
            quality_target_kbps: 10000,
            export_encoder_key: "x264-medium".to_string(),
            export_bitrate_scale: 1.0,
            audio_gain_db: 0.0,
            unsupported_encoder_keys: Vec::new(),
            encoder_benchmarks: Vec::new(),
            include_audio: true,
            audio_device_name: String::new(),
            start_with_windows: false,
            record_hotkey: "Super+Shift+R".to_string(),
            reset_hotkey: "Super+Shift+4".to_string(),
            github_repository_url: "https://github.com/daveranan/clipper".to_string(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EncoderBenchmark {
    encoder_key: String,
    encoder_label: String,
    seconds: f64,
    bytes: u64,
    tested_at: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VideoInfo {
    id: String,
    path: String,
    original_path: String,
    width: u32,
    height: u32,
    duration_seconds: f64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreviewCache {
    folder: String,
    fps: u32,
    frames: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WaveformPeakData {
    duration_seconds: f64,
    sample_rate: u32,
    channels: Vec<WaveformPeakChannel>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WaveformPeakChannel {
    minimums: Vec<f32>,
    maximums: Vec<f32>,
}

#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Crop {
    x: u32,
    y: u32,
    width: u32,
    height: u32,
}

#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct CutRange {
    start: f64,
    end: f64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportRequest {
    input_path: String,
    output_path: String,
    start: f64,
    end: f64,
    audio_start: f64,
    audio_end: f64,
    crop: Crop,
    output_width: u32,
    output_height: u32,
    auto_fit720: bool,
    cuts: Vec<CutRange>,
    audio_cuts: Vec<CutRange>,
    settings: AppSettings,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportResult {
    path: String,
    bytes: u64,
    seconds: f64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BenchmarkResult {
    encoder_key: String,
    encoder_label: String,
    path: String,
    bytes: u64,
    seconds: f64,
    success: bool,
    error: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordingRequest {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
    settings: AppSettings,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordingRegion {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

#[tauri::command]
fn load_settings() -> Result<AppSettings, String> {
    let path = settings_path()?;
    if !path.exists() {
        return Ok(AppSettings::default());
    }

    let json = fs::read_to_string(path).map_err(|error| error.to_string())?;
    serde_json::from_str(&json).map_err(|error| error.to_string())
}

#[tauri::command]
fn save_settings(settings: AppSettings) -> Result<(), String> {
    let path = settings_path()?;
    ensure_parent(&path)?;
    let json = serde_json::to_string_pretty(&settings).map_err(|error| error.to_string())?;
    fs::write(path, json).map_err(|error| error.to_string())
}

#[tauri::command]
fn open_video_dialog(settings: AppSettings) -> Result<Option<VideoInfo>, String> {
    let Some(source_path) = rfd::FileDialog::new()
        .add_filter("Video", &["mp4", "mov", "mkv", "webm", "avi", "wmv", "m4v", "gif"])
        .pick_file()
    else {
        return Ok(None);
    };

    let imported = import_to_workspace(&source_path)?;
    probe_video_file(&settings, &imported, &source_path)
}

#[tauri::command]
fn prepare_preview_cache(input_path: String, fps: u32, max_seconds: f64, settings: AppSettings) -> Result<PreviewCache, String> {
    let folder = preview_root()?.join(Uuid::new_v4().to_string());
    fs::create_dir_all(&folder).map_err(|error| error.to_string())?;
    let pattern = folder.join("frame_%06d.jpg");
    let fps = fps.clamp(4, 12);
    let max_seconds = max_seconds.clamp(1.0, 180.0);
    let filter = format!("fps={},scale=1280:-2", fps);
    run_command(
        &settings.ffmpeg_path,
        &[
            "-hide_banner",
            "-y",
            "-i",
            &input_path,
            "-t",
            &seconds(max_seconds),
            "-vf",
            &filter,
            "-q:v",
            "5",
            &pattern.to_string_lossy(),
        ],
        "Preview cache failed",
    )?;

    let mut frames: Vec<String> = fs::read_dir(&folder)
        .map_err(|error| error.to_string())?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| path.extension().is_some_and(|extension| extension == "jpg"))
        .map(|path| path.to_string_lossy().to_string())
        .collect();
    frames.sort();

    Ok(PreviewCache {
        folder: folder.to_string_lossy().to_string(),
        fps,
        frames,
    })
}

#[tauri::command]
fn prepare_playback_source(input_path: String, settings: AppSettings) -> Result<String, String> {
    if is_browser_safe_playback(&settings, Path::new(&input_path))? {
        return Ok(input_path);
    }

    let proxy = preview_root()?.join(format!("playback-{}.mp4", Uuid::new_v4()));
    let args = [
        "-hide_banner".to_string(),
        "-y".to_string(),
        "-i".to_string(),
        input_path,
        "-map".to_string(),
        "0:v:0".to_string(),
        "-map".to_string(),
        "0:a?".to_string(),
        "-c:v".to_string(),
        "libx264".to_string(),
        "-preset".to_string(),
        "veryfast".to_string(),
        "-crf".to_string(),
        "12".to_string(),
        "-pix_fmt".to_string(),
        "yuv420p".to_string(),
        "-c:a".to_string(),
        "aac".to_string(),
        "-b:a".to_string(),
        "128k".to_string(),
        "-movflags".to_string(),
        "+faststart".to_string(),
        proxy.to_string_lossy().to_string(),
    ];
    let refs: Vec<&str> = args.iter().map(String::as_str).collect();
    run_command(&settings.ffmpeg_path, &refs, "Playback proxy failed")?;
    Ok(proxy.to_string_lossy().to_string())
}

#[tauri::command]
fn extract_exact_frame(input_path: String, seconds_at: f64, settings: AppSettings) -> Result<String, String> {
    let folder = preview_root()?.join("exact");
    fs::create_dir_all(&folder).map_err(|error| error.to_string())?;
    let frame_path = folder.join(format!("frame-{}-{}.jpg", Uuid::new_v4(), millis(seconds_at)));
    run_command(
        &settings.ffmpeg_path,
        &[
            "-hide_banner",
            "-y",
            "-i",
            &input_path,
            "-ss",
            &seconds(seconds_at.max(0.0)),
            "-frames:v",
            "1",
            "-update",
            "1",
            "-q:v",
            "2",
            &frame_path.to_string_lossy(),
        ],
        "Exact frame failed",
    )?;
    Ok(frame_path.to_string_lossy().to_string())
}

#[tauri::command]
fn generate_waveform(input_path: String, points: u32, settings: AppSettings) -> Result<WaveformPeakData, String> {
    let probe = probe_video_file(&settings, Path::new(&input_path), Path::new(&input_path))?
        .ok_or_else(|| "Could not read media duration.".to_string())?;
    let sample_rate = 24_000_u32;
    let points = points.clamp(512, 65_536);
    let sample_count = ((probe.duration_seconds.max(0.1) * sample_rate as f64).ceil() as usize).max(points as usize);
    let samples_per_peak = (sample_count as f64 / points as f64).ceil().max(1.0) as usize;
    let mut command = hidden_command(&settings.ffmpeg_path);
    command.args([
        "-hide_banner",
        "-v",
        "error",
        "-i",
        &input_path,
        "-map",
        "0:a:0?",
        "-ac",
        "1",
        "-ar",
        &sample_rate.to_string(),
        "-f",
        "f32le",
        "-",
    ]);
    let output = command
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .map_err(|error| format!("Waveform generation failed: {}", error))?;
    if !output.status.success() || output.stdout.is_empty() {
        return Ok(WaveformPeakData {
            duration_seconds: probe.duration_seconds,
            sample_rate,
            channels: Vec::new(),
        });
    }

    let samples: Vec<f32> = output
        .stdout
        .chunks_exact(4)
        .map(|bytes| f32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]).clamp(-1.0, 1.0))
        .collect();
    let mut minimums = Vec::new();
    let mut maximums = Vec::new();
    for chunk in samples.chunks(samples_per_peak) {
        let mut min_value = 0.0_f32;
        let mut max_value = 0.0_f32;
        for sample in chunk {
            let sample = *sample;
            min_value = min_value.min(sample);
            max_value = max_value.max(sample);
        }
        minimums.push(min_value);
        maximums.push(max_value);
    }

    Ok(WaveformPeakData {
        duration_seconds: probe.duration_seconds,
        sample_rate,
        channels: vec![WaveformPeakChannel { minimums, maximums }],
    })
}

#[tauri::command]
fn choose_export_path(save_folder: String, file_name: String) -> Result<Option<String>, String> {
    fs::create_dir_all(&save_folder).map_err(|error| error.to_string())?;
    let path = rfd::FileDialog::new()
        .set_directory(save_folder)
        .set_file_name(file_name)
        .add_filter("MP4 Video", &["mp4"])
        .save_file();
    Ok(path.map(|path| path.to_string_lossy().to_string()))
}

#[tauri::command]
fn choose_save_folder(current: String) -> Result<Option<String>, String> {
    let mut dialog = rfd::FileDialog::new();
    if !current.trim().is_empty() {
        dialog = dialog.set_directory(current);
    }
    Ok(dialog.pick_folder().map(|path| path.to_string_lossy().to_string()))
}

#[tauri::command]
fn choose_ffmpeg_path() -> Result<Option<String>, String> {
    Ok(rfd::FileDialog::new()
        .add_filter("FFmpeg", &["exe"])
        .set_file_name("ffmpeg.exe")
        .pick_file()
        .map(|path| path.to_string_lossy().to_string()))
}

#[tauri::command]
fn list_audio_devices(settings: AppSettings) -> Result<Vec<String>, String> {
    let output = hidden_command(&settings.ffmpeg_path)
        .args(["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"])
        .output()
        .map_err(|error| format!("Audio device scan failed: {}", error))?;
    let text = String::from_utf8_lossy(&output.stderr);
    let mut devices = Vec::new();
    for line in text.lines() {
        if !line.contains("(audio)") {
            continue;
        }
        if let Some(start) = line.find('"') {
            if let Some(end) = line[start + 1..].find('"') {
                let name = line[start + 1..start + 1 + end].to_string();
                if !devices.contains(&name) {
                    devices.push(name);
                }
            }
        }
    }
    Ok(devices)
}

#[tauri::command]
fn open_region_selector() -> Result<Option<RecordingRegion>, String> {
    let script = r#"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeDpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
try { [NativeDpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { try { [NativeDpi]::SetProcessDPIAware() | Out-Null } catch {} }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$script:start = $null
$script:current = $null
$script:result = $null
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = $bounds
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.BackColor = [System.Drawing.Color]::Black
$form.Opacity = 0.35
$form.Cursor = [System.Windows.Forms.Cursors]::Cross
$form.KeyPreview = $true
$form.Add_KeyDown({ if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Escape) { $script:result = 'cancel'; $form.Close() } })
$form.Add_MouseDown({
  if ($_.Button -eq [System.Windows.Forms.MouseButtons]::Right) {
    $script:result = 'cancel'
    $form.Close()
    return
  }
  if ($_.Button -ne [System.Windows.Forms.MouseButtons]::Left) { return }
  $script:start = New-Object System.Drawing.Point -ArgumentList ($_.X + $bounds.Left), ($_.Y + $bounds.Top)
  $script:current = $script:start
  $form.Invalidate()
})
$form.Add_MouseMove({
  if ($script:start -eq $null) { return }
  $script:current = New-Object System.Drawing.Point -ArgumentList ($_.X + $bounds.Left), ($_.Y + $bounds.Top)
  $form.Invalidate()
})
$form.Add_MouseUp({
  if ($script:start -eq $null) { return }
  if ($_.Button -ne [System.Windows.Forms.MouseButtons]::Left) { return }
  $end = New-Object System.Drawing.Point -ArgumentList ($_.X + $bounds.Left), ($_.Y + $bounds.Top)
  $x = [Math]::Min($script:start.X, $end.X)
  $y = [Math]::Min($script:start.Y, $end.Y)
  $w = [Math]::Abs($end.X - $script:start.X)
  $h = [Math]::Abs($end.Y - $script:start.Y)
  if ($w -ge 8 -and $h -ge 8) {
    $script:result = @{ x = $x; y = $y; width = $w; height = $h } | ConvertTo-Json -Compress
  } else {
    $script:result = 'cancel'
  }
  $form.Close()
})
$form.Add_Paint({
  if ($script:start -eq $null -or $script:current -eq $null) { return }
  $left = [Math]::Min($script:start.X, $script:current.X) - $bounds.Left
  $top = [Math]::Min($script:start.Y, $script:current.Y) - $bounds.Top
  $width = [Math]::Abs($script:current.X - $script:start.X)
  $height = [Math]::Abs($script:current.Y - $script:start.Y)
  if ($width -le 0 -or $height -le 0) { return }
  $selection = New-Object System.Drawing.Rectangle -ArgumentList $left, $top, $width, $height
  $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 65, 214, 195))
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 65, 214, 195), 2)
  $_.Graphics.FillRectangle($brush, $selection)
  $_.Graphics.DrawRectangle($pen, $selection)
  $brush.Dispose()
  $pen.Dispose()
})
$form.Add_Shown({ $form.Activate(); $form.Focus() })
[void]$form.ShowDialog()
if ($script:result -and $script:result -ne 'cancel') { Write-Output $script:result }
"#;

    let output = hidden_command("powershell.exe")
        .args(["-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-Command", script])
        .output()
        .map_err(|error| format!("Region selector failed: {}", error))?;
    if !output.status.success() {
        return Err(format!("Region selector failed: {}", String::from_utf8_lossy(&output.stderr).trim()));
    }
    let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if text.is_empty() {
        return Ok(None);
    }
    serde_json::from_str::<RecordingRegion>(&text)
        .map(Some)
        .map_err(|error| format!("Region selector returned invalid data: {}", error))
}

#[tauri::command]
fn export_clip(request: ExportRequest) -> Result<ExportResult, String> {
    export_with_encoder(&request, &request.settings.export_encoder_key, &request.output_path)
}

fn export_with_encoder(request: &ExportRequest, encoder_key: &str, output_path: &str) -> Result<ExportResult, String> {
    let started = Instant::now();
    ensure_parent(Path::new(output_path))?;
    let duration = kept_duration(request.start, request.end, &request.cuts);
    if duration <= 0.01 {
        return Err("Trim range is empty.".to_string());
    }

    let source_path = Path::new(&request.input_path);
    let (source_width, source_height) = source_video_dimensions(&request.settings, source_path)?;
    let source_has_audio = source_has_audio(&request.settings, source_path);
    let export_crop = clamp_export_crop(&request.crop, source_width, source_height);
    let video_kept = kept_segments(request.start, request.end, &request.cuts);
    let audio_kept = if request.settings.include_audio {
        kept_segments(request.audio_start, request.audio_end, &request.audio_cuts)
    } else {
        Vec::new()
    };
    let has_audio_output = source_has_audio && !audio_kept.is_empty();
    let audio_edit_matches_video = ranges_equal(&video_kept, &audio_kept);
    let audio_kbps = if has_audio_output { 96 } else { 0 };
    let video_kbps = calculate_video_bitrate(request.settings.max_megabytes, duration, audio_kbps);
    let mut args = vec!["-hide_banner".to_string(), "-y".to_string(), "-i".to_string(), request.input_path.clone()];

    if request.cuts.is_empty() && (!has_audio_output || audio_edit_matches_video) {
        args.extend([
            "-ss".to_string(),
            seconds(request.start),
            "-t".to_string(),
            seconds(duration),
        ]);
        let filters = build_filters(&request, &export_crop);
        if !filters.is_empty() {
            args.extend(["-vf".to_string(), filters]);
        }
        args.extend(["-map".to_string(), "0:v:0".to_string()]);
        if has_audio_output {
            args.extend(["-map".to_string(), "0:a?".to_string(), "-c:a".to_string(), "aac".to_string(), "-b:a".to_string(), "96k".to_string()]);
            if request.settings.audio_gain_db.abs() > 0.01 {
                args.extend(["-af".to_string(), format!("volume={:.6}", db_to_linear(request.settings.audio_gain_db))]);
            }
        } else {
            args.push("-an".to_string());
        }
    } else {
        let (filter_complex, has_audio) = build_cut_filter(&request, &export_crop, source_has_audio);
        args.extend(["-filter_complex".to_string(), filter_complex]);
        if has_audio {
            args.extend(["-map".to_string(), "[outv]".to_string(), "-map".to_string(), "[outa]".to_string(), "-c:a".to_string(), "aac".to_string(), "-b:a".to_string(), "96k".to_string()]);
        } else {
            args.extend(["-map".to_string(), "[outv]".to_string(), "-an".to_string()]);
        }
    }

    args.extend(["-r".to_string(), request.settings.frame_rate.clamp(5, 60).to_string()]);
    args.extend(encoder_args(encoder_key));
    args.extend([
        "-b:v".to_string(),
        format!("{}k", video_kbps),
        "-maxrate".to_string(),
        format!("{}k", video_kbps),
        "-bufsize".to_string(),
        format!("{}k", video_kbps * 2),
        "-movflags".to_string(),
        "+faststart".to_string(),
        "-pix_fmt".to_string(),
        "yuv420p".to_string(),
        output_path.to_string(),
    ]);

    let arg_refs: Vec<&str> = args.iter().map(String::as_str).collect();
    if let Err(error) = run_command(&request.settings.ffmpeg_path, &arg_refs, "Export failed") {
        remove_empty_file(Path::new(output_path));
        return Err(error);
    }
    let bytes = fs::metadata(output_path).map_err(|error| error.to_string())?.len();

    Ok(ExportResult {
        path: output_path.to_string(),
        bytes,
        seconds: started.elapsed().as_secs_f64(),
    })
}

#[tauri::command]
fn benchmark_encoders(request: ExportRequest) -> Result<Vec<BenchmarkResult>, String> {
    let mut results = Vec::new();
    let output = PathBuf::from(&request.output_path);
    let folder = output
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_else(|| PathBuf::from("."));
    let stem = output
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("benchmark");

    for (encoder_key, encoder_label) in encoder_presets() {
        if request
            .settings
            .unsupported_encoder_keys
            .iter()
            .any(|key| key == encoder_key)
        {
            continue;
        }
        let path = folder.join(format!("{}-{}.mp4", stem, encoder_key));
        match export_with_encoder(&request, encoder_key, &path.to_string_lossy()) {
            Ok(result) => results.push(BenchmarkResult {
                encoder_key: encoder_key.to_string(),
                encoder_label: encoder_label.to_string(),
                path: result.path,
                bytes: result.bytes,
                seconds: result.seconds,
                success: true,
                error: String::new(),
            }),
            Err(error) => results.push(BenchmarkResult {
                encoder_key: encoder_key.to_string(),
                encoder_label: encoder_label.to_string(),
                path: path.to_string_lossy().to_string(),
                bytes: 0,
                seconds: 0.0,
                success: false,
                error,
            }),
        }
    }

    if results.iter().all(|result| !result.success) {
        return Err(results
            .iter()
            .map(|result| format!("{}: {}", result.encoder_label, result.error))
            .collect::<Vec<_>>()
            .join("\n"));
    }

    Ok(results)
}

#[tauri::command]
fn start_recording(request: RecordingRequest, state: State<'_, AppState>) -> Result<String, String> {
    let mut recording = state.recording.lock().map_err(|_| "Recording lock failed.".to_string())?;
    if recording.is_some() {
        return Err("Recording is already running.".to_string());
    }

    let active = spawn_recording(request)?;
    let path = active.path.clone();
    *recording = Some(active);
    Ok(path.to_string_lossy().to_string())
}

fn spawn_recording(request: RecordingRequest) -> Result<ActiveRecording, String> {
    let path = media_root()?.join(format!("recording-{}.mp4", timestamp()));
    let width = make_even(request.width).max(2);
    let height = make_even(request.height).max(2);
    let mut args = vec![
        "-hide_banner".to_string(),
        "-stats_period".to_string(),
        "0.1".to_string(),
        "-y".to_string(),
        "-f".to_string(),
        "gdigrab".to_string(),
        "-framerate".to_string(),
        request.settings.frame_rate.clamp(5, 60).to_string(),
        "-offset_x".to_string(),
        request.x.to_string(),
        "-offset_y".to_string(),
        request.y.to_string(),
        "-video_size".to_string(),
        format!("{}x{}", width, height),
        "-i".to_string(),
        "desktop".to_string(),
    ];

    args.push("-an".to_string());

    args.extend([
        "-c:v".to_string(),
        "libx264".to_string(),
        "-preset".to_string(),
        "ultrafast".to_string(),
        "-crf".to_string(),
        "12".to_string(),
        "-pix_fmt".to_string(),
        "yuv420p".to_string(),
        "-tune".to_string(),
        "zerolatency".to_string(),
        "-movflags".to_string(),
        "+faststart".to_string(),
        path.to_string_lossy().to_string(),
    ]);

    let mut command = hidden_command(&request.settings.ffmpeg_path);
    let mut child = command
        .args(args)
        .stdin(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| format!("Could not start FFmpeg: {}", error))?;

    thread::sleep(Duration::from_millis(250));
    if child.try_wait().map_err(|error| error.to_string())?.is_some() {
        let mut stderr = String::new();
        if let Some(mut pipe) = child.stderr.take() {
            let _ = pipe.read_to_string(&mut stderr);
        }
        return Err(format!("Recording failed to start: {}", stderr.trim()));
    }

    let audio = if request.settings.include_audio {
        Some(start_loopback_recording()?)
    } else {
        None
    };
    let overlay_child = start_recording_overlay(request.x, request.y, width, height).ok();
    Ok(ActiveRecording { child, overlay_child, audio, path })
}

#[tauri::command]
fn stop_recording(settings: AppSettings, state: State<'_, AppState>) -> Result<VideoInfo, String> {
    let mut guard = state.recording.lock().map_err(|_| "Recording lock failed.".to_string())?;
    let Some(mut recording) = guard.take() else {
        return Err("No recording is running.".to_string());
    };

    stop_recording_overlay(&mut recording.overlay_child);
    if let Some(stdin) = recording.child.stdin.as_mut() {
        if let Err(error) = stdin.write_all(b"q\n") {
            if error.kind() != std::io::ErrorKind::BrokenPipe {
                return Err(error.to_string());
            }
        }
    }
    let output = recording.child.wait_with_output().map_err(|error| error.to_string())?;
    if !output.status.success() {
        return Err(format!("Recording failed: {}", String::from_utf8_lossy(&output.stderr)));
    }
    if let Some(audio_path) = stop_loopback_recording(recording.audio.take())? {
        mux_recording_audio(&settings.ffmpeg_path, &recording.path, &audio_path)?;
    }

    probe_video_file(&settings, &recording.path, &recording.path)?
        .ok_or_else(|| "Recording did not produce a usable video stream.".to_string())
}

#[tauri::command]
fn reset_recording(request: RecordingRequest, state: State<'_, AppState>) -> Result<String, String> {
    let mut guard = state.recording.lock().map_err(|_| "Recording lock failed.".to_string())?;
    if let Some(mut recording) = guard.take() {
        let path = recording.path.clone();
        stop_recording_overlay(&mut recording.overlay_child);
        if let Some(stdin) = recording.child.stdin.as_mut() {
            let _ = stdin.write_all(b"q\n");
        }
        let _ = recording.child.wait();
        if let Ok(Some(audio_path)) = stop_loopback_recording(recording.audio.take()) {
            let _ = fs::remove_file(audio_path);
        }
        let _ = fs::remove_file(path);
    }

    let active = spawn_recording(request)?;
    let path = active.path.clone();
    *guard = Some(active);
    Ok(path.to_string_lossy().to_string())
}

#[tauri::command]
fn reveal_path(path: String) -> Result<(), String> {
    let target = PathBuf::from(path);
    let mut command = Command::new("explorer.exe");
    if target.is_file() {
        command.arg(format!("/select,{}", target.to_string_lossy()));
    } else {
        command.arg(target);
    }
    command.spawn().map_err(|error| error.to_string())?;
    Ok(())
}

#[tauri::command]
fn copy_file_to_clipboard(path: String) -> Result<(), String> {
    let path = PathBuf::from(path);
    if !path.is_file() {
        return Err("Export file does not exist.".to_string());
    }
    let mut clipboard = arboard::Clipboard::new().map_err(|error| format!("Could not open clipboard: {}", error))?;
    clipboard
        .set()
        .file_list(&[path])
        .map_err(|error| format!("Could not copy file to clipboard: {}", error))
}

fn probe_video_file(settings: &AppSettings, media_path: &Path, original_path: &Path) -> Result<Option<VideoInfo>, String> {
    let ffprobe = sibling_tool(&settings.ffmpeg_path, "ffprobe.exe");
    let output = hidden_command(&ffprobe)
        .args([
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height:format=duration",
            "-of",
            "default=noprint_wrappers=1",
            &media_path.to_string_lossy(),
        ])
        .output()
        .map_err(|error| error.to_string())?;

    if !output.status.success() {
        return Err(String::from_utf8_lossy(&output.stderr).to_string());
    }

    let text = String::from_utf8_lossy(&output.stdout);
    let mut width = 0;
    let mut height = 0;
    let mut duration_seconds = 0.0;
    for line in text.lines() {
        if let Some(value) = line.strip_prefix("width=") {
            width = value.parse().unwrap_or(0);
        } else if let Some(value) = line.strip_prefix("height=") {
            height = value.parse().unwrap_or(0);
        } else if let Some(value) = line.strip_prefix("duration=") {
            duration_seconds = value.parse().unwrap_or(0.0);
        }
    }

    if width == 0 || height == 0 || duration_seconds <= 0.0 {
        return Ok(None);
    }

    Ok(Some(VideoInfo {
        id: Uuid::new_v4().to_string(),
        path: media_path.to_string_lossy().to_string(),
        original_path: original_path.to_string_lossy().to_string(),
        width,
        height,
        duration_seconds,
    }))
}

fn is_browser_safe_playback(settings: &AppSettings, media_path: &Path) -> Result<bool, String> {
    let extension = media_path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    if extension != "mp4" && extension != "m4v" {
        return Ok(false);
    }

    let ffprobe = sibling_tool(&settings.ffmpeg_path, "ffprobe.exe");
    let output = hidden_command(&ffprobe)
        .args([
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=codec_name,pix_fmt",
            "-of",
            "default=noprint_wrappers=1",
            &media_path.to_string_lossy(),
        ])
        .output()
        .map_err(|error| error.to_string())?;
    if !output.status.success() {
        return Ok(false);
    }

    let text = String::from_utf8_lossy(&output.stdout);
    let mut codec = String::new();
    let mut pix_fmt = String::new();
    for line in text.lines() {
        if let Some(value) = line.strip_prefix("codec_name=") {
            codec = value.to_string();
        } else if let Some(value) = line.strip_prefix("pix_fmt=") {
            pix_fmt = value.to_string();
        }
    }
    Ok(codec == "h264" && pix_fmt == "yuv420p")
}

fn source_video_dimensions(settings: &AppSettings, media_path: &Path) -> Result<(u32, u32), String> {
    let ffprobe = sibling_tool(&settings.ffmpeg_path, "ffprobe.exe");
    let output = hidden_command(&ffprobe)
        .args([
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            "stream=width,height",
            "-of",
            "default=noprint_wrappers=1",
            &media_path.to_string_lossy(),
        ])
        .output()
        .map_err(|error| error.to_string())?;
    if !output.status.success() {
        return Err(command_error_message("Source probe failed", &String::from_utf8_lossy(&output.stderr)));
    }

    let text = String::from_utf8_lossy(&output.stdout);
    let mut width = 0;
    let mut height = 0;
    for line in text.lines() {
        if let Some(value) = line.strip_prefix("width=") {
            width = value.parse().unwrap_or(0);
        } else if let Some(value) = line.strip_prefix("height=") {
            height = value.parse().unwrap_or(0);
        }
    }
    if width < 2 || height < 2 {
        return Err("Source probe failed: invalid video dimensions.".to_string());
    }
    Ok((width, height))
}

fn source_has_audio(settings: &AppSettings, media_path: &Path) -> bool {
    let ffprobe = sibling_tool(&settings.ffmpeg_path, "ffprobe.exe");
    hidden_command(&ffprobe)
        .args([
            "-v",
            "error",
            "-select_streams",
            "a:0",
            "-show_entries",
            "stream=index",
            "-of",
            "csv=p=0",
            &media_path.to_string_lossy(),
        ])
        .output()
        .map(|output| output.status.success() && !String::from_utf8_lossy(&output.stdout).trim().is_empty())
        .unwrap_or(false)
}

fn import_to_workspace(source: &Path) -> Result<PathBuf, String> {
    let extension = source.extension().and_then(|value| value.to_str()).unwrap_or("mp4");
    let destination = media_root()?.join(format!("source-{}.{}", timestamp(), extension));
    fs::copy(source, &destination).map_err(|error| error.to_string())?;
    Ok(destination)
}

fn settings_path() -> Result<PathBuf, String> {
    Ok(app_root()?.join("settings.json"))
}

fn app_root() -> Result<PathBuf, String> {
    let path = dirs::data_local_dir()
        .ok_or_else(|| "Could not resolve local app data directory.".to_string())?
        .join("QuickClipper");
    fs::create_dir_all(&path).map_err(|error| error.to_string())?;
    Ok(path)
}

fn media_root() -> Result<PathBuf, String> {
    let path = app_root()?.join("Media");
    fs::create_dir_all(&path).map_err(|error| error.to_string())?;
    Ok(path)
}

fn preview_root() -> Result<PathBuf, String> {
    let path = app_root()?.join("Preview");
    fs::create_dir_all(&path).map_err(|error| error.to_string())?;
    Ok(path)
}

fn ensure_parent(path: &Path) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    Ok(())
}

fn sibling_tool(ffmpeg_path: &str, tool: &str) -> String {
    let ffmpeg = Path::new(ffmpeg_path);
    if ffmpeg.is_absolute() {
        if let Some(parent) = ffmpeg.parent() {
            let sibling = parent.join(tool);
            if sibling.exists() {
                return sibling.to_string_lossy().to_string();
            }
        }
    }
    tool.to_string()
}

fn hidden_command(program: &str) -> Command {
    let mut command = Command::new(program);
    #[cfg(windows)]
    command.creation_flags(0x08000000);
    command
}

#[cfg(windows)]
fn start_loopback_recording() -> Result<LoopbackRecording, String> {
    let stop = Arc::new(AtomicBool::new(false));
    let worker_stop = Arc::clone(&stop);
    let path = std::env::temp_dir().join(format!("quickclipper-audio-{}.wav", timestamp()));
    let thread = thread::spawn(move || capture_loopback_to_wav(worker_stop, path));
    Ok(LoopbackRecording { stop, thread })
}

#[cfg(not(windows))]
fn start_loopback_recording() -> Result<LoopbackRecording, String> {
    Err("System audio capture is only available on Windows.".to_string())
}

fn stop_loopback_recording(recording: Option<LoopbackRecording>) -> Result<Option<PathBuf>, String> {
    let Some(recording) = recording else {
        return Ok(None);
    };
    recording.stop.store(true, Ordering::SeqCst);
    recording
        .thread
        .join()
        .map_err(|_| "Audio capture thread panicked.".to_string())?
}

#[cfg(windows)]
fn capture_loopback_to_wav(stop: Arc<AtomicBool>, path: PathBuf) -> Result<Option<PathBuf>, String> {
    use std::{ptr::null_mut, slice};
    use windows::{
        Win32::{
            Media::Audio::{
                eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator,
                MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK, WAVEFORMATEX,
            },
            System::{
                Com::{CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL, COINIT_MULTITHREADED},
                Threading::Sleep,
            },
        },
    };

    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED)
            .ok()
            .map_err(|error| error.to_string())?;
        let result = (|| -> Result<Option<PathBuf>, String> {
            let enumerator: IMMDeviceEnumerator =
                CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).map_err(|error| error.to_string())?;
            let device = enumerator
                .GetDefaultAudioEndpoint(eRender, eConsole)
                .map_err(|error| error.to_string())?;
            let audio_client: IAudioClient = device
                .Activate(CLSCTX_ALL, None)
                .map_err(|error| error.to_string())?;
            let mix_format = audio_client.GetMixFormat().map_err(|error| error.to_string())?;
            if mix_format.is_null() {
                return Err("Windows returned an empty audio mix format.".to_string());
            }

            let format = *mix_format;
            let frame_bytes = format.nBlockAlign as usize;
            if frame_bytes == 0 {
                CoTaskMemFree(Some(mix_format.cast()));
                return Err("Windows returned an invalid audio block alignment.".to_string());
            }

            audio_client
                .Initialize(
                    AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_LOOPBACK,
                    1_000_000,
                    0,
                    mix_format,
                    None,
                )
                .map_err(|error| error.to_string())?;

            let capture_client: IAudioCaptureClient = audio_client.GetService().map_err(|error| error.to_string())?;
            let mut file = fs::File::create(&path).map_err(|error| error.to_string())?;
            let format_bytes = slice::from_raw_parts(
                mix_format.cast::<u8>(),
                std::mem::size_of::<WAVEFORMATEX>() + format.cbSize as usize,
            )
            .to_vec();
            write_wave_header(&mut file, &format_bytes, 0)?;

            audio_client.Start().map_err(|error| error.to_string())?;
            let mut data_bytes: u32 = 0;
            while !stop.load(Ordering::SeqCst) {
                let mut next_packet_frames = capture_client.GetNextPacketSize().map_err(|error| error.to_string())?;
                if next_packet_frames == 0 {
                    Sleep(10);
                    continue;
                }

                while next_packet_frames > 0 {
                    let mut data = null_mut();
                    let mut frames = 0u32;
                    let mut flags = Default::default();
                    capture_client
                        .GetBuffer(&mut data, &mut frames, &mut flags, None, None)
                        .map_err(|error| error.to_string())?;
                    let bytes = frames as usize * frame_bytes;
                    if bytes > 0 {
                        if (flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32) != 0 || data.is_null() {
                            file.write_all(&vec![0u8; bytes]).map_err(|error| error.to_string())?;
                        } else {
                            file.write_all(slice::from_raw_parts(data.cast::<u8>(), bytes))
                                .map_err(|error| error.to_string())?;
                        }
                        data_bytes = data_bytes.saturating_add(bytes as u32);
                    }
                    capture_client.ReleaseBuffer(frames).map_err(|error| error.to_string())?;
                    next_packet_frames = capture_client.GetNextPacketSize().map_err(|error| error.to_string())?;
                }
            }

            let _ = audio_client.Stop();
            finalize_wave_header(&mut file, data_bytes)?;
            CoTaskMemFree(Some(mix_format.cast()));
            if data_bytes == 0 {
                let _ = fs::remove_file(&path);
                Ok(None)
            } else {
                Ok(Some(path))
            }
        })();
        CoUninitialize();
        result
    }
}

fn write_wave_header(file: &mut fs::File, format_bytes: &[u8], data_bytes: u32) -> Result<(), String> {
    let riff_bytes = 4u32
        .saturating_add(8)
        .saturating_add(format_bytes.len() as u32)
        .saturating_add(8)
        .saturating_add(data_bytes);
    file.seek(SeekFrom::Start(0)).map_err(|error| error.to_string())?;
    file.write_all(b"RIFF").map_err(|error| error.to_string())?;
    file.write_all(&riff_bytes.to_le_bytes()).map_err(|error| error.to_string())?;
    file.write_all(b"WAVEfmt ").map_err(|error| error.to_string())?;
    file.write_all(&(format_bytes.len() as u32).to_le_bytes())
        .map_err(|error| error.to_string())?;
    file.write_all(format_bytes).map_err(|error| error.to_string())?;
    file.write_all(b"data").map_err(|error| error.to_string())?;
    file.write_all(&data_bytes.to_le_bytes()).map_err(|error| error.to_string())?;
    Ok(())
}

fn finalize_wave_header(file: &mut fs::File, data_bytes: u32) -> Result<(), String> {
    file.flush().map_err(|error| error.to_string())?;
    let current = file.stream_position().map_err(|error| error.to_string())?;
    file.seek(SeekFrom::Start(4)).map_err(|error| error.to_string())?;
    file.write_all(&(current.saturating_sub(8) as u32).to_le_bytes())
        .map_err(|error| error.to_string())?;
    let data_size_offset = current.saturating_sub(data_bytes as u64).saturating_sub(4);
    file.seek(SeekFrom::Start(data_size_offset))
        .map_err(|error| error.to_string())?;
    file.write_all(&data_bytes.to_le_bytes()).map_err(|error| error.to_string())?;
    file.seek(SeekFrom::End(0)).map_err(|error| error.to_string())?;
    file.flush().map_err(|error| error.to_string())?;
    Ok(())
}

fn mux_recording_audio(ffmpeg_path: &str, video_path: &Path, audio_path: &Path) -> Result<(), String> {
    if fs::metadata(audio_path).map(|metadata| metadata.len()).unwrap_or(0) <= 64 {
        let _ = fs::remove_file(audio_path);
        return Ok(());
    }
    let muxed = video_path.with_file_name(format!("quickclipper-muxed-{}.mp4", timestamp()));
    let args = [
        "-hide_banner".to_string(),
        "-y".to_string(),
        "-i".to_string(),
        video_path.to_string_lossy().to_string(),
        "-i".to_string(),
        audio_path.to_string_lossy().to_string(),
        "-map".to_string(),
        "0:v:0".to_string(),
        "-map".to_string(),
        "1:a:0".to_string(),
        "-c:v".to_string(),
        "copy".to_string(),
        "-c:a".to_string(),
        "aac".to_string(),
        "-b:a".to_string(),
        "128k".to_string(),
        "-shortest".to_string(),
        "-avoid_negative_ts".to_string(),
        "make_zero".to_string(),
        "-movflags".to_string(),
        "+faststart".to_string(),
        muxed.to_string_lossy().to_string(),
    ];
    let refs: Vec<&str> = args.iter().map(String::as_str).collect();
    run_command(ffmpeg_path, &refs, "Audio mux failed")?;
    fs::copy(&muxed, video_path).map_err(|error| error.to_string())?;
    let _ = fs::remove_file(muxed);
    let _ = fs::remove_file(audio_path);
    Ok(())
}

fn start_recording_overlay(x: i32, y: i32, width: u32, height: u32) -> Result<Child, String> {
    let script = r#"
$X = __X__
$Y = __Y__
$W = __W__
$H = __H__
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeWindowStyles {
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
try { [NativeWindowStyles]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { try { [NativeWindowStyles]::SetProcessDPIAware() | Out-Null } catch {} }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$transparent = [System.Drawing.Color]::FromArgb(255, 255, 0, 255)
$record = [System.Drawing.Color]::FromArgb(229, 72, 77)
$window = New-Object System.Windows.Forms.Form
$window.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$window.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$window.Bounds = New-Object System.Drawing.Rectangle -ArgumentList ($X - 3), ($Y - 36), ($W + 6), ($H + 42)
$window.TopMost = $true
$window.ShowInTaskbar = $false
$window.BackColor = $transparent
$window.TransparencyKey = $transparent
$timerText = New-Object System.Windows.Forms.Label
$timerText.Text = '00:00'
$timerText.AutoSize = $true
$timerText.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 4, 8, 4
$timerText.BackColor = [System.Drawing.Color]::FromArgb(204, 17, 17, 17)
$timerText.ForeColor = [System.Drawing.Color]::White
$timerText.Font = New-Object System.Drawing.Font -ArgumentList $timerText.Font, ([System.Drawing.FontStyle]::Bold)
$timerText.Location = New-Object System.Drawing.Point -ArgumentList 0, 0
$window.Controls.Add($timerText)
foreach ($panel in @(
  @{ X = 0; Y = 33; W = $W + 6; H = 3 },
  @{ X = 0; Y = 33; W = 3; H = $H + 6 },
  @{ X = $W + 3; Y = 33; W = 3; H = $H + 6 },
  @{ X = 0; Y = $H + 36; W = $W + 6; H = 3 }
)) {
  $edge = New-Object System.Windows.Forms.Panel
  $edge.BackColor = $record
  $edge.Bounds = New-Object System.Drawing.Rectangle -ArgumentList $panel.X, $panel.Y, $panel.W, $panel.H
  $window.Controls.Add($edge)
}
$started = [DateTime]::Now
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 250
$timer.Add_Tick({
  $elapsed = [DateTime]::Now - $started
  if ($elapsed.TotalHours -ge 1) { $timerText.Text = $elapsed.ToString('hh\:mm\:ss') } else { $timerText.Text = $elapsed.ToString('mm\:ss') }
})
$window.Add_Shown({
  $hwnd = $window.Handle
  $style = [NativeWindowStyles]::GetWindowLong($hwnd, -20)
  [NativeWindowStyles]::SetWindowLong($hwnd, -20, $style -bor 0x20 -bor 0x80000) | Out-Null
  $timer.Start()
})
$window.Add_Closed({ $timer.Stop() })
[void]$window.ShowDialog()
"#
    .replace("__X__", &x.to_string())
    .replace("__Y__", &y.to_string())
    .replace("__W__", &width.to_string())
    .replace("__H__", &height.to_string());
    hidden_command("powershell.exe")
        .args([
            "-NoProfile",
            "-STA",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            &script,
        ])
        .spawn()
        .map_err(|error| format!("Recording overlay failed: {}", error))
}

fn stop_recording_overlay(child: &mut Option<Child>) {
    if let Some(mut overlay) = child.take() {
        let _ = overlay.kill();
        let _ = overlay.wait();
    }
}

fn run_command(exe: &str, args: &[&str], label: &str) -> Result<(), String> {
    let output = hidden_command(exe)
        .args(args)
        .output()
        .map_err(|error| format!("{}: {}", label, error))?;
    if output.status.success() {
        Ok(())
    } else {
        Err(command_error_message(label, &String::from_utf8_lossy(&output.stderr)))
    }
}

fn command_error_message(label: &str, stderr: &str) -> String {
    let lines = stderr.lines().map(str::trim).filter(|line| !line.is_empty()).collect::<Vec<_>>();
    let preferred = lines
        .iter()
        .rev()
        .find(|line| {
            let lower = line.to_ascii_lowercase();
            lower.contains("invalid")
                || lower.contains("error")
                || lower.contains("failed")
                || lower.contains("could not")
                || lower.contains("nothing was written")
                || lower.contains("conversion failed")
        })
        .or_else(|| {
            lines.iter().rev().find(|line| {
                !line.starts_with("Input #")
                    && !line.starts_with("Metadata:")
                    && !line.starts_with("Duration:")
                    && !line.starts_with("Stream #")
                    && !line.starts_with("Press [q]")
                    && !line.starts_with("frame=")
            })
        })
        .copied()
        .unwrap_or("process exited with an error");
    format!("{}: {}", label, preferred)
}

fn remove_empty_file(path: &Path) {
    if fs::metadata(path).map(|metadata| metadata.len()).unwrap_or(1) == 0 {
        let _ = fs::remove_file(path);
    }
}

fn clamp_export_crop(crop: &Crop, source_width: u32, source_height: u32) -> Crop {
    let x = even_floor(crop.x.min(source_width.saturating_sub(2)));
    let y = even_floor(crop.y.min(source_height.saturating_sub(2)));
    let max_width = source_width.saturating_sub(x).max(2);
    let max_height = source_height.saturating_sub(y).max(2);
    Crop {
        x,
        y,
        width: make_even(crop.width.min(max_width).max(2)),
        height: make_even(crop.height.min(max_height).max(2)),
    }
}

fn build_filters(request: &ExportRequest, crop: &Crop) -> String {
    let mut filters = Vec::new();
    if crop.width > 0 && crop.height > 0 {
        filters.push(format!(
            "crop={}:{}:{}:{}",
            make_even(crop.width),
            make_even(crop.height),
            crop.x,
            crop.y
        ));
    }

    if request.auto_fit720 {
        filters.push("crop=min(iw\\,ih*16/9):min(ih\\,iw*9/16):(iw-ow)/2:(ih-oh)/2".to_string());
        filters.push("scale=1280:720".to_string());
    } else if request.output_width > 0 && request.output_height > 0 {
        filters.push(format!("scale={}:{}", make_even(request.output_width), make_even(request.output_height)));
    }

    filters.join(",")
}

fn build_cut_filter(request: &ExportRequest, crop: &Crop, source_has_audio: bool) -> (String, bool) {
    let video_kept = kept_segments(request.start, request.end, &request.cuts);
    let audio_kept = if request.settings.include_audio && source_has_audio {
        kept_segments(request.audio_start, request.audio_end, &request.audio_cuts)
    } else {
        Vec::new()
    };
    let filters = build_filters(request, crop);
    let has_audio = !audio_kept.is_empty();
    let mut parts = Vec::new();
    for (index, (start, end)) in video_kept.iter().enumerate() {
        let mut video = format!("[0:v]trim=start={}:end={},setpts=PTS-STARTPTS", seconds(*start), seconds(*end));
        if !filters.is_empty() {
            video.push(',');
            video.push_str(&filters);
        }
        video.push_str(&format!("[v{}]", index));
        parts.push(video);
    }

    let video_inputs = (0..video_kept.len()).map(|index| format!("[v{}]", index)).collect::<Vec<_>>().join("");
    parts.push(format!("{}concat=n={}:v=1:a=0[outv]", video_inputs, video_kept.len()));

    if has_audio {
        for (index, (start, end)) in audio_kept.iter().enumerate() {
            parts.push(format!(
                "[0:a]atrim=start={}:end={},asetpts=PTS-STARTPTS,volume={:.6}[a{}]",
                seconds(*start),
                seconds(*end),
                db_to_linear(request.settings.audio_gain_db),
                index
            ));
        }
        let audio_inputs = (0..audio_kept.len()).map(|index| format!("[a{}]", index)).collect::<Vec<_>>().join("");
        parts.push(format!("{}concat=n={}:v=0:a=1[outa]", audio_inputs, audio_kept.len()));
    }
    (parts.join(";"), has_audio)
}

fn kept_segments(start: f64, end: f64, cuts: &[CutRange]) -> Vec<(f64, f64)> {
    let mut ranges = cuts.to_vec();
    ranges.sort_by(|a, b| a.start.partial_cmp(&b.start).unwrap_or(std::cmp::Ordering::Equal));
    let mut cursor = start;
    let mut kept = Vec::new();
    for cut in ranges {
        let cut_start = cut.start.max(start);
        let cut_end = cut.end.min(end);
        if cut_end <= cut_start {
            continue;
        }
        if cut_start > cursor {
            kept.push((cursor, cut_start));
        }
        cursor = cursor.max(cut_end);
    }
    if cursor < end {
        kept.push((cursor, end));
    }
    kept
}

fn kept_duration(start: f64, end: f64, cuts: &[CutRange]) -> f64 {
    kept_segments(start, end, cuts)
        .iter()
        .map(|(segment_start, segment_end)| segment_end - segment_start)
        .sum()
}

fn ranges_equal(left: &[(f64, f64)], right: &[(f64, f64)]) -> bool {
    left.len() == right.len()
        && left
            .iter()
            .zip(right.iter())
            .all(|((left_start, left_end), (right_start, right_end))| {
                (left_start - right_start).abs() < 0.001 && (left_end - right_end).abs() < 0.001
            })
}

fn calculate_video_bitrate(max_mb: f64, duration: f64, audio_kbps: u32) -> u32 {
    let total_kbits = max_mb.max(0.1) * 8192.0 * 0.985;
    ((total_kbits / duration.max(0.5)) as i32 - audio_kbps as i32).clamp(250, 12000) as u32
}

fn db_to_linear(db: f64) -> f64 {
    10_f64.powf(db.clamp(-24.0, 36.0) / 20.0)
}

fn encoder_args(key: &str) -> Vec<String> {
    match key {
        "x264-slow" => vec!["-c:v", "libx264", "-preset", "slow"],
        "x264-veryslow" => vec!["-c:v", "libx264", "-preset", "veryslow"],
        "x265-medium" => vec!["-c:v", "libx265", "-preset", "medium"],
        "x265-slow" => vec!["-c:v", "libx265", "-preset", "slow"],
        "h264-nvenc-fast" => vec!["-c:v", "h264_nvenc", "-preset", "p1"],
        "h264-nvenc" => vec!["-c:v", "h264_nvenc", "-preset", "p5"],
        "h264-nvenc-max" => vec!["-c:v", "h264_nvenc", "-preset", "p7"],
        "hevc-nvenc-fast" => vec!["-c:v", "hevc_nvenc", "-preset", "p1"],
        "hevc-nvenc" => vec!["-c:v", "hevc_nvenc", "-preset", "p5"],
        "hevc-nvenc-max" => vec!["-c:v", "hevc_nvenc", "-preset", "p7"],
        _ => vec!["-c:v", "libx264", "-preset", "medium"],
    }
    .into_iter()
    .map(str::to_string)
    .collect()
}

fn encoder_presets() -> Vec<(&'static str, &'static str)> {
    vec![
        ("x264-medium", "H.264 Medium"),
        ("x264-slow", "H.264 Slow"),
        ("x264-veryslow", "H.264 Veryslow"),
        ("x265-medium", "H.265 Medium"),
        ("x265-slow", "H.265 Slow"),
        ("h264-nvenc-fast", "H.264 NVENC Fast"),
        ("h264-nvenc", "H.264 NVENC Quality"),
        ("h264-nvenc-max", "H.264 NVENC Max"),
        ("hevc-nvenc-fast", "H.265 NVENC Fast"),
        ("hevc-nvenc", "H.265 NVENC Quality"),
        ("hevc-nvenc-max", "H.265 NVENC Max"),
    ]
}

fn make_even(value: u32) -> u32 {
    if value % 2 == 0 { value } else { value.saturating_sub(1).max(2) }
}

fn even_floor(value: u32) -> u32 {
    value.saturating_sub(value % 2)
}

fn seconds(value: f64) -> String {
    format!("{:.3}", value.max(0.0))
}

fn millis(value: f64) -> u64 {
    (value.max(0.0) * 1000.0).round() as u64
}

fn timestamp() -> String {
    Local::now().format("%Y%m%d-%H%M%S").to_string()
}

fn create_tray(app: &tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let open = MenuItem::with_id(app, "open", "Open QuickClipper", true, None::<&str>)?;
    let record = MenuItem::with_id(app, "record", "Record / Stop", true, None::<&str>)?;
    let reset = MenuItem::with_id(app, "reset", "Reset Recording", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&open, &record, &reset, &quit])?;

    let mut tray = TrayIconBuilder::with_id("quickclipper")
        .tooltip("QuickClipper")
        .menu(&menu);
    if let Some(icon) = app.default_window_icon().cloned() {
        tray = tray.icon(icon);
    }
    tray
        .on_menu_event(|app, event| match event.id.as_ref() {
            "open" => show_main_window(app),
            "record" => {
                let _ = app.emit("tray-record-toggle", ());
            }
            "reset" => {
                let _ = app.emit("tray-record-reset", ());
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle());
            }
        })
        .build(app)?;

    Ok(())
}

fn focus_webview_window(window: &tauri::WebviewWindow) -> Result<(), String> {
    window.show().map_err(|error| error.to_string())?;
    let _ = window.unminimize();
    let _ = window.set_always_on_top(true);
    thread::sleep(Duration::from_millis(80));
    let _ = window.set_always_on_top(false);
    window.set_focus().map_err(|error| error.to_string())
}

#[tauri::command]
fn focus_main_window(app: tauri::AppHandle) -> Result<(), String> {
    let Some(window) = app.get_webview_window("main") else {
        return Err("Main window is not available.".to_string());
    };
    focus_webview_window(&window)
}

fn show_main_window(app: &tauri::AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = focus_webview_window(&window);
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(AppState {
            recording: Mutex::new(None),
        })
        .plugin(tauri_plugin_global_shortcut::Builder::new().build())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(
            tauri_plugin_autostart::Builder::new()
                .app_name("QuickClipper")
                .build(),
        )
        .plugin(tauri_plugin_clipboard_manager::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_process::init())
        .setup(|app| {
            create_tray(app)?;
            if cfg!(debug_assertions) {
                app.handle().plugin(
                    tauri_plugin_log::Builder::default()
                        .level(log::LevelFilter::Info)
                        .build(),
                )?;
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            load_settings,
            save_settings,
            open_video_dialog,
            prepare_preview_cache,
            prepare_playback_source,
            extract_exact_frame,
            choose_export_path,
            choose_save_folder,
            choose_ffmpeg_path,
            list_audio_devices,
            generate_waveform,
            open_region_selector,
            export_clip,
            benchmark_encoders,
            start_recording,
            stop_recording,
            reset_recording,
            focus_main_window,
            copy_file_to_clipboard,
            reveal_path
        ])
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                let _ = window.hide();
                api.prevent_close();
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
