using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using Forms = System.Windows.Forms;

namespace QuickClipper;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly FfmpegService _ffmpeg = new();
    private readonly HotKeyService _hotKey = new();
    private readonly StartupService _startupService = new();
    private readonly UpdateService _updateService = new();
    private readonly DispatcherTimer _playTimer = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private AppSettings _settings;
    private ActiveRecording? _recording;
    private RecordingOverlayWindow? _recordingOverlay;
    private Forms.ToolStripMenuItem? _recordMenuItem;
    private string? _currentClip;
    private string? _lastExport;
    private string? _previewFrameFolder;
    private string? _previewAudioPath;
    private List<string> _previewFrames = new();
    private WaveOutEvent? _previewAudioOutput;
    private AudioFileReader? _previewAudioReader;
    private CaptureRegion _lastRegion;
    private TimeSpan _clipDuration = TimeSpan.Zero;
    private bool _isPlaying;
    private bool _allowClose;
    private bool _isSelectingOrStarting;
    private bool _isCheckingUpdates;
    private bool _isUpdatingCropText;
    private bool _isUpdatingOutputSize;
    private bool _isLoadingSettings;
    private bool _previewAudioLoading;
    private bool _autoFit720;
    private double _trimStartSeconds;
    private double _trimEndSeconds;
    private double _currentSeconds;
    private double _previewFps = 30;
    private readonly List<CutRange> _cutRanges = new();
    private double? _pendingCutStartSeconds;
    private int _dragCutIndex = -1;
    private DateTime _playbackStartedAt;
    private double _playbackStartSeconds;
    private TimelineDragTarget _dragTarget = TimelineDragTarget.None;
    private CropDragTarget _cropDragTarget = CropDragTarget.None;
    private System.Windows.Point _cropDragStartPoint;
    private CaptureRegion _cropDragStartRegion;
    private IntPtr _hotKeyWindowHandle;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        _settings = _settingsService.Load();
        _trayIcon = CreateTrayIcon();
        LoadSettingsIntoUi();
        UpdateSettingsSummary();
        _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _settings.FrameRate));
        _playTimer.Tick += PlayTimer_Tick;
        TimelineCanvas.SizeChanged += (_, _) => UpdateTimelineVisuals();
        SourceInitialized += MainWindow_SourceInitialized;
        SetClipLoaded(false);
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void LoadWindowIcon()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        if (icon is null)
        {
            return;
        }

        Icon = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    public void ExitApplication()
    {
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hotKey.Dispose();
        Close();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotKeyWindowHandle = new WindowInteropHelper(this).Handle;
        ApplyHotKeys();
        _hotKey.Pressed += async (_, _) => await ToggleRecordingAsync();
        _hotKey.ResetPressed += async (_, _) => await ResetRecordingAsync();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        _recordMenuItem = new Forms.ToolStripMenuItem("Record", null, async (_, _) => await ToggleRecordingAsync());
        menu.Items.Add(_recordMenuItem);
        menu.Items.Add("Reset Recording", null, async (_, _) => await ResetRecordingAsync());
        menu.Items.Add("Check for Updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "QuickClipper",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseClick += async (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                await ToggleRecordingAsync();
            }
        };
        return icon;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                return icon;
            }
        }

        return System.Drawing.SystemIcons.Application;
    }

    private async Task ToggleRecordingAsync()
    {
        if (_isSelectingOrStarting)
        {
            return;
        }

        if (_recording is not null)
        {
            await StopRecordingAsync();
            return;
        }

        await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        _isSelectingOrStarting = true;
        try
        {
            SaveSettingsFromUi();
            Hide();

            var overlay = new OverlayWindow();
            var region = await overlay.SelectRegionAsync();
            if (region is null || !region.Value.IsValid)
            {
                Hide();
                return;
            }

            await StartRecordingInRegionAsync(region.Value);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ShowTrayMessage("Recording failed", ex.Message);
            ShowWindow();
        }
        finally
        {
            _isSelectingOrStarting = false;
        }
    }

    private async Task StartRecordingInRegionAsync(CaptureRegion region)
    {
        _recording = await _ffmpeg.StartRecordingAsync(_settings, region);
        _lastRegion = _recording.Region;
        _recordingOverlay?.Close();
        _recordingOverlay = new RecordingOverlayWindow(_lastRegion);
        _recordingOverlay.Start();
        RecordButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = $"Recording. {_settings.RecordHotKey.Label} stops. {_settings.ResetHotKey.Label} resets.";
        SetRecordingUi(true);
    }

    private async Task ResetRecordingAsync()
    {
        if (_recording is null)
        {
            return;
        }

        var region = _recording.Region;
        var oldRecording = _recording;
        _recording = null;
        await _ffmpeg.StopRecordingAsync(oldRecording, muxAudio: false);
        _recordingOverlay?.Close();
        _recordingOverlay = null;
        TryDelete(oldRecording.OutputPath);
        try
        {
            await StartRecordingInRegionAsync(region);
        }
        catch (Exception ex)
        {
            SetRecordingUi(false);
            ShowTrayMessage("Reset failed", ex.Message);
            ShowWindow();
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_recording is null)
        {
            return;
        }

        var recording = _recording;
        _recording = null;
        await _ffmpeg.StopRecordingAsync(recording, muxAudio: false);
        _recordingOverlay?.Close();
        _recordingOverlay = null;
        RecordButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetRecordingUi(false);
        if (!File.Exists(recording.OutputPath) ||
            new FileInfo(recording.OutputPath).Length < 4096 ||
            !await _ffmpeg.HasVideoStreamAsync(_settings, recording.OutputPath))
        {
            StatusText.Text = $"Recording failed: {recording.ErrorLog}";
            ShowTrayMessage("Recording failed", "FFmpeg did not create a usable clip.");
            ShowWindow();
            return;
        }

        StatusText.Text = "Finalizing audio...";
        ShowWindow();
        await _ffmpeg.FinalizeRecordingAudioAsync(recording);
        if (!await _ffmpeg.HasVideoStreamAsync(_settings, recording.OutputPath))
        {
            StatusText.Text = "Recording failed: finalized clip has no video stream.";
            return;
        }

        LoadClip(recording.OutputPath, recording.Region);
    }

    private async void LoadClip(string path, CaptureRegion region)
    {
        _playTimer.Stop();
        _isPlaying = false;
        PlayPauseButton.Content = "Play";
        _currentClip = path;
        _lastRegion = region;
        _lastExport = null;
        DisposePreviewAudio();
        _previewAudioLoading = _settings.IncludeAudio;
        SetClipLoaded(true);
        CropXText.Text = "0";
        CropYText.Text = "0";
        CropWidthText.Text = region.Width.ToString();
        CropHeightText.Text = region.Height.ToString();
        _autoFit720 = false;
        SetOutputSize(region.Width, region.Height);
        ClipInfoText.Text = path;
        StatusText.Text = "Clip ready. Preparing lightweight preview...";

        _clipDuration = await _ffmpeg.GetDurationAsync(_settings, path);
        _previewFps = Math.Clamp(_settings.FrameRate, 5, 12);
        _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _previewFps);
        PreparePreviewFolder();
        _previewAudioPath = Path.Combine(_previewFrameFolder!, "preview.wav");
        _currentSeconds = 0;
        _trimStartSeconds = 0;
        _trimEndSeconds = Math.Max(_clipDuration.TotalSeconds, 0.1);
        _cutRanges.Clear();
        _pendingCutStartSeconds = null;
        CutButton.Content = "Start Cut";
        UpdateTrimText();
        UpdateCropOverlayFromText();
        UpdateQualityBudgetText();
        _ = PreparePreviewAsync(path);
    }

    private async Task PreparePreviewAsync(string path)
    {
        try
        {
            await _ffmpeg.ExtractFrameAsync(_settings, path, TimeSpan.Zero, Path.Combine(_previewFrameFolder!, "frame_000001.jpg"));
            _previewFrames = Directory.GetFiles(_previewFrameFolder!, "frame_*.jpg").OrderBy(file => file).ToList();
            ShowFrameAtCurrentTime();
            var audioTask = Task.Run(async () =>
            {
                try
                {
                    await _ffmpeg.ExtractAudioPreviewAsync(_settings, path, _previewAudioPath!);
                    await Dispatcher.InvokeAsync(LoadPreviewAudio);
                }
                finally
                {
                    await Dispatcher.InvokeAsync(() => _previewAudioLoading = false);
                }
            });

            await Task.Delay(100);
            var frameTask = _ffmpeg.ExtractPreviewFramesAsync(_settings, path, _previewFrameFolder!, (int)_previewFps);
            await Task.WhenAll(frameTask, audioTask);
            _previewFrames = Directory.GetFiles(_previewFrameFolder!, "frame_*.jpg").OrderBy(file => file).ToList();
            StatusText.Text = "Clip ready. Space plays/pauses. Press C to create cuts.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ShortError(ex.Message);
        }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        await StartRecordingAsync();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopRecordingAsync();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
    }

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCutAtCurrentTime();
    }

    private void RemoveCutButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveCutAtCurrentTime();
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentClip is null || !_isPlaying)
        {
            return;
        }

        var previousSeconds = _currentSeconds;
        _currentSeconds = _previewAudioReader is not null
            ? _previewAudioReader.CurrentTime.TotalSeconds
            : _playbackStartSeconds + (DateTime.Now - _playbackStartedAt).TotalSeconds;

        var skippedTo = GetPlaybackSkipTarget(previousSeconds, _currentSeconds);
        if (skippedTo is not null)
        {
            SeekPlayback(skippedTo.Value);
        }

        if (_currentSeconds >= _trimEndSeconds)
        {
            _isPlaying = false;
            _playTimer.Stop();
            PlayPauseButton.Content = "Play";
            _previewAudioOutput?.Pause();
            _currentSeconds = _trimStartSeconds;
        }

        ShowFrameAtCurrentTime();
        UpdateTrimText();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        SaveSettingsFromUi();
        var output = Path.Combine(_settings.SaveFolder, $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        ExportOptions options;
        try
        {
            options = BuildExportOptions(output);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        try
        {
            StatusText.Text = $"Exporting toward {_settings.MaxMegabytes:0.##} MB...";
            ExportButton.IsEnabled = false;
            ExportProgressBar.Visibility = Visibility.Visible;
            var results = await _ffmpeg.ExportAsync(_settings, options);
            var successes = results.Where(result => result.Success).ToList();
            var failures = results.Where(result => !result.Success).ToList();
            _lastExport = successes.Last().Path;
            CopyButton.IsEnabled = true;
            StatusText.Text = successes.Count == 1
                ? $"Exported {Path.GetFileName(_lastExport)} ({FormatBytes(successes[0].Bytes)})"
                : $"Exported {successes.Count} test files; {failures.Count} failed/unavailable";
            ClipInfoText.Text = string.Join(" | ", results.Select(result =>
                result.Success
                    ? $"{result.EncoderLabel}: {FormatBytes(result.Bytes)}"
                    : $"{result.EncoderLabel}: unavailable"));
        }
        catch (Exception ex)
        {
            StatusText.Text = ShortError(ex.Message);
        }
        finally
        {
            ExportProgressBar.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = _currentClip is not null;
        }
    }

    private async void BenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            StatusText.Text = "Record a clip before benchmarking.";
            return;
        }

        SaveSettingsFromUi();
        var previousEncoder = _settings.ExportEncoderKey;
        _settings.ExportEncoderKey = "all-test";
        var output = Path.Combine(_settings.SaveFolder, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var options = BuildExportOptions(output);

        try
        {
            StatusText.Text = "Benchmarking encoders...";
            ExportButton.IsEnabled = false;
            ExportProgressBar.Visibility = Visibility.Visible;
            var results = await _ffmpeg.ExportAsync(_settings, options);
            results = await AttachBenchmarkFramesAsync(results, options);
            SaveBenchmarkResults(results);
            UpdateEncoderComboLabels();
            new BenchmarkReportWindow(results) { Owner = this }.ShowDialog();
            StatusText.Text = "Benchmark complete.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ShortError(ex.Message);
        }
        finally
        {
            _settings.ExportEncoderKey = previousEncoder;
            _settingsService.Save(_settings);
            ExportProgressBar.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = _currentClip is not null;
        }
    }

    private async Task<IReadOnlyList<ExportResult>> AttachBenchmarkFramesAsync(IReadOnlyList<ExportResult> results, ExportOptions options)
    {
        var frameTime = TimeSpan.FromSeconds(Math.Clamp(_currentSeconds - _trimStartSeconds, 0, Math.Max(0, options.Duration.TotalSeconds - 0.033)));
        var folder = Path.Combine(Path.GetTempPath(), $"quickclipper-benchmark-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var updated = new List<ExportResult>();

        foreach (var result in results)
        {
            if (!result.Success)
            {
                updated.Add(result);
                continue;
            }

            var framePath = Path.Combine(folder, $"{result.EncoderKey}.jpg");
            await _ffmpeg.ExtractFrameAsync(_settings, result.Path, frameTime, framePath);
            updated.Add(result with { PreviewFramePath = framePath });
        }

        return updated;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExport is null || !File.Exists(_lastExport))
        {
            return;
        }

        var files = new StringCollection { _lastExport };
        System.Windows.Clipboard.SetFileDropList(files);
        StatusText.Text = $"Copied to clipboard: {Path.GetFileName(_lastExport)}";
        ClipInfoText.Text = $"Copied to clipboard: {_lastExport}";
    }

    private ExportOptions BuildExportOptions(string output)
    {
        ApplyQualityTrimCap();
        var start = TimeSpan.FromSeconds(_trimStartSeconds);
        var end = TimeSpan.FromSeconds(_trimEndSeconds);
        var cuts = GetExportCutRanges(start, end);
        var duration = CalculateKeptDuration(start, end, cuts);
        if (duration.TotalSeconds <= 0)
        {
            throw new InvalidOperationException("Trim range is empty.");
        }

        return new ExportOptions(
            _currentClip!,
            output,
            start,
            end,
            duration,
            ReadInt(CropXText, 0),
            ReadInt(CropYText, 0),
            ReadInt(CropWidthText, _lastRegion.Width),
            ReadInt(CropHeightText, _lastRegion.Height),
            ReadInt(OutputWidthText, _lastRegion.Width),
            ReadInt(OutputHeightText, _lastRegion.Height),
            _autoFit720,
            cuts);
    }

    private IReadOnlyList<CutRange> GetExportCutRanges(TimeSpan start, TimeSpan end)
    {
        MergeCutRanges();
        return _cutRanges
            .Select(cut => new CutRange(Max(cut.Start, start), Min(cut.End, end)))
            .Where(cut => cut.End > cut.Start)
            .ToList();
    }

    private static TimeSpan CalculateKeptDuration(TimeSpan start, TimeSpan end, IReadOnlyList<CutRange> cuts)
    {
        var kept = end - start;
        foreach (var cut in cuts)
        {
            kept -= cut.End - cut.Start;
        }

        return kept > TimeSpan.Zero ? kept : TimeSpan.Zero;
    }

    private void SaveBenchmarkResults(IReadOnlyList<ExportResult> results)
    {
        _settings.EncoderBenchmarks = results
            .Where(result => result.Success)
            .Select(result => new EncoderBenchmark
            {
                EncoderKey = result.EncoderKey,
                EncoderLabel = result.EncoderLabel,
                Bytes = result.Bytes,
                Seconds = result.Duration.TotalSeconds,
                TestedAt = DateTime.Now
            })
            .ToList();

        foreach (var failure in results.Where(result => !result.Success))
        {
            if (!_settings.UnsupportedEncoderKeys.Contains(failure.EncoderKey))
            {
                _settings.UnsupportedEncoderKeys.Add(failure.EncoderKey);
            }
        }

        _settingsService.Save(_settings);
    }

    private void BrowseSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(SaveFolderText.Text) ? SaveFolderText.Text : _settings.SaveFolder
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SaveFolderText.Text = dialog.SelectedPath;
        }
    }

    private void OpenSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        Directory.CreateDirectory(_settings.SaveFolder);
        Process.Start(new ProcessStartInfo
        {
            FileName = _settings.SaveFolder,
            UseShellExecute = true
        });
        StatusText.Text = $"Opened save folder: {_settings.SaveFolder}";
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "FFmpeg|ffmpeg.exe|Executables|*.exe|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            FfmpegPathText.Text = dialog.FileName;
        }
    }

    private void AudioSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        UpdateQualityBudgetText();
    }

    private void QualitySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
        UpdateQualityBudgetText();
    }

    private void QualityText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || !IsLoaded)
        {
            return;
        }

        SaveSettingsFromUi();
        UpdateQualityBudgetText();
    }

    private void StartupSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
    }

    private void EncoderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveSettingsFromUi();
    }

    private void OutputSizeText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingOutputSize || _lastRegion.Width <= 0 || _lastRegion.Height <= 0)
        {
            return;
        }

        _autoFit720 = false;
        var crop = ReadCropRegion();
        if (sender == OutputWidthText)
        {
            var width = ReadInt(OutputWidthText, crop.Width);
            var height = Math.Max(1, (int)Math.Round(width * (double)crop.Height / Math.Max(crop.Width, 1)));
            SetOutputSize(width, height);
            UpdateQualityBudgetText();
        }
        else if (sender == OutputHeightText)
        {
            var height = ReadInt(OutputHeightText, crop.Height);
            var width = Math.Max(1, (int)Math.Round(height * (double)crop.Width / Math.Max(crop.Height, 1)));
            SetOutputSize(width, height);
            UpdateQualityBudgetText();
        }
    }

    private void Auto720Button_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRegion.Width <= 0 || _lastRegion.Height <= 0)
        {
            return;
        }

        var crop = ReadCropRegion();
        var targetHeight = MakeEven((int)Math.Round(crop.Width * 9.0 / 16.0));
        if (targetHeight <= crop.Height)
        {
            crop = new CaptureRegion(crop.X, crop.Y + (crop.Height - targetHeight) / 2, crop.Width, targetHeight);
        }
        else
        {
            var targetWidth = MakeEven((int)Math.Round(crop.Height * 16.0 / 9.0));
            crop = new CaptureRegion(crop.X + Math.Max(0, (crop.Width - targetWidth) / 2), crop.Y, Math.Min(crop.Width, targetWidth), crop.Height);
        }

        _autoFit720 = true;
        SetCropRegion(ClampCrop(crop), updateResize: false);
        SetOutputSize(1280, 720);
        UpdateQualityBudgetText();
        StatusText.Text = "Auto 1280 x 720 set.";
    }

    private void ApplyQualityTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        SaveSettingsFromUi();
        var changed = ApplyQualityTrimCap();
        StatusText.Text = changed
            ? $"Trim capped for quality: {FormatTime(_trimStartSeconds)} to {FormatTime(_trimEndSeconds)}."
            : "Current trim already fits the quality cap.";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        UpdateSettingsSummary();
        StatusText.Text = "Settings saved.";
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        await CheckForUpdatesAsync(manual: true);
    }

    private void RecordHotKeyText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        CaptureHotKey(e, hotKey => _settings.RecordHotKey = hotKey, RecordHotKeyText);
    }

    private void ResetHotKeyText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        CaptureHotKey(e, hotKey => _settings.ResetHotKey = hotKey, ResetHotKeyText);
    }

    private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clipDuration.TotalSeconds <= 0)
        {
            return;
        }

        TimelineCanvas.CaptureMouse();
        var x = e.GetPosition(TimelineCanvas).X;
        _dragTarget = PickTimelineTarget(x);
        ApplyTimelineDrag(x);
    }

    private void TimelineCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTarget != TimelineDragTarget.None)
        {
            ApplyTimelineDrag(e.GetPosition(TimelineCanvas).X);
        }
    }

    private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragTarget = TimelineDragTarget.None;
        _dragCutIndex = -1;
        TimelineCanvas.ReleaseMouseCapture();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space && !(Keyboard.FocusedElement is System.Windows.Controls.TextBox))
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.C && !(Keyboard.FocusedElement is System.Windows.Controls.TextBox))
        {
            ToggleCutAtCurrentTime();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !(Keyboard.FocusedElement is System.Windows.Controls.TextBox))
        {
            RemoveCutAtCurrentTime();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _pendingCutStartSeconds is not null)
        {
            _pendingCutStartSeconds = null;
            CutButton.Content = "Start Cut";
            StatusText.Text = "Pending cut canceled.";
            UpdateTimelineVisuals();
            e.Handled = true;
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void SetRecordingUi(bool isRecording)
    {
        _trayIcon.Text = isRecording ? "QuickClipper - Recording" : "QuickClipper";
        if (_recordMenuItem is not null)
        {
            _recordMenuItem.Text = isRecording ? "Stop Recording" : "Record";
        }

        return;
    }

    private void SetClipLoaded(bool loaded)
    {
        if (EmptyPreviewPanel is not null)
        {
            EmptyPreviewPanel.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        }

        if (ExportButton is not null)
        {
            ExportButton.IsEnabled = loaded;
        }

        if (CopyButton is not null)
        {
            CopyButton.IsEnabled = loaded && _lastExport is not null && File.Exists(_lastExport);
        }

        if (PlayPauseButton is not null)
        {
            PlayPauseButton.IsEnabled = loaded;
        }

        if (CutButton is not null)
        {
            CutButton.IsEnabled = loaded;
        }

        if (RemoveCutButton is not null)
        {
            RemoveCutButton.IsEnabled = loaded;
        }
    }

    private void ShowTrayMessage(string title, string message)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message.Length > 180 ? message[..180] : message;
        _trayIcon.ShowBalloonTip(3000);
    }

    private void CaptureHotKey(System.Windows.Input.KeyEventArgs e, Action<HotKeyBinding> assign, System.Windows.Controls.TextBox textBox)
    {
        e.Handled = true;
        var hotKey = HotKeyBinding.FromKeyEvent(e);
        if (!hotKey.IsValid)
        {
            StatusText.Text = "Use at least one modifier plus a normal key.";
            return;
        }

        assign(hotKey);
        textBox.Text = hotKey.Label;
        StatusText.Text = "Hotkey captured. Save settings to apply.";
    }

    private void ApplyHotKeys()
    {
        if (_hotKeyWindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_settings.RecordHotKey.SameAs(_settings.ResetHotKey))
        {
            StatusText.Text = "Record and reset hotkeys must be different.";
            return;
        }

        try
        {
            _hotKey.Register(_hotKeyWindowHandle, _settings.RecordHotKey, _settings.ResetHotKey);
            StatusText.Text = $"{_settings.RecordHotKey.Label} to select an area.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ShortError(ex.Message);
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_isCheckingUpdates)
        {
            return;
        }

        _isCheckingUpdates = true;
        try
        {
            if (manual)
            {
                StatusText.Text = "Checking for updates...";
            }

            var result = await _updateService.CheckDownloadAndRestartAsync(
                _settings.GitHubRepositoryUrl,
                message => Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(message, "QuickClipper update", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes).Task,
                progress => Dispatcher.Invoke(() => StatusText.Text = $"Downloading update {progress}%..."));

            if (manual || result.Kind == UpdateCheckResultKind.Restarting)
            {
                StatusText.Text = result.Message;
                ShowTrayMessage("QuickClipper update", result.Message);
            }
        }
        catch (Exception ex)
        {
            if (manual)
            {
                StatusText.Text = ShortError(ex.Message);
                ShowTrayMessage("Update failed", ex.Message);
            }
        }
        finally
        {
            _isCheckingUpdates = false;
        }
    }

    private void TogglePlayback()
    {
        if (_currentClip is null || _previewFrames.Count == 0)
        {
            return;
        }

        if (!_isPlaying && _previewAudioLoading && _previewAudioReader is null)
        {
            StatusText.Text = "Audio preview is still loading.";
            return;
        }

        if (!_isPlaying && (_currentSeconds < _trimStartSeconds || _currentSeconds >= _trimEndSeconds))
        {
            _currentSeconds = _trimStartSeconds;
            ShowFrameAtCurrentTime();
        }

        var skipTarget = GetPlaybackSkipTarget(_currentSeconds, _currentSeconds);
        if (!_isPlaying && skipTarget is not null)
        {
            _currentSeconds = skipTarget.Value;
            ShowFrameAtCurrentTime();
        }

        _isPlaying = !_isPlaying;
        PlayPauseButton.Content = _isPlaying ? "Pause" : "Play";
        if (_isPlaying)
        {
            _playbackStartSeconds = _currentSeconds;
            _playbackStartedAt = DateTime.Now;
            if (_previewAudioReader is not null)
            {
                _previewAudioReader.CurrentTime = TimeSpan.FromSeconds(_currentSeconds);
                _previewAudioOutput?.Play();
            }
            _playTimer.Start();
        }
        else
        {
            _previewAudioOutput?.Pause();
            _playTimer.Stop();
        }
    }

    private void PreparePreviewFolder()
    {
        DisposePreviewAudio();
        if (!string.IsNullOrWhiteSpace(_previewFrameFolder))
        {
            TryDeleteDirectory(_previewFrameFolder);
        }

        _previewFrameFolder = Path.Combine(Path.GetTempPath(), $"quickclipper-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_previewFrameFolder);
    }

    private void LoadPreviewAudio()
    {
        DisposePreviewAudio();
        if (string.IsNullOrWhiteSpace(_previewAudioPath) || !File.Exists(_previewAudioPath))
        {
            return;
        }

        try
        {
            _previewAudioReader = new AudioFileReader(_previewAudioPath);
            _previewAudioOutput = new WaveOutEvent();
            _previewAudioOutput.Init(_previewAudioReader);
        }
        catch
        {
            DisposePreviewAudio();
        }
    }

    private void DisposePreviewAudio()
    {
        _previewAudioOutput?.Stop();
        _previewAudioOutput?.Dispose();
        _previewAudioReader?.Dispose();
        _previewAudioOutput = null;
        _previewAudioReader = null;
    }

    private void ShowFrameAtCurrentTime()
    {
        if (_previewFrames.Count == 0)
        {
            return;
        }

        var frameIndex = Math.Clamp((int)Math.Round(_currentSeconds * _previewFps), 0, _previewFrames.Count - 1);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(_previewFrames[frameIndex]);
        bitmap.EndInit();
        bitmap.Freeze();
        PreviewFrame.Source = bitmap;
    }

    private void SeekPreview(double seconds)
    {
        _currentSeconds = Math.Clamp(seconds, 0, Math.Max(_clipDuration.TotalSeconds, 0.1));
        if (_isPlaying)
        {
            _playbackStartSeconds = _currentSeconds;
            _playbackStartedAt = DateTime.Now;
            if (_previewAudioReader is not null)
            {
                _previewAudioReader.CurrentTime = TimeSpan.FromSeconds(_currentSeconds);
            }
        }
        else
        {
            if (_previewAudioReader is not null)
            {
                _previewAudioReader.CurrentTime = TimeSpan.FromSeconds(_currentSeconds);
            }
        }

        ShowFrameAtCurrentTime();
        UpdateTrimText();
    }

    private void SeekPlayback(double seconds)
    {
        _currentSeconds = Math.Clamp(seconds, _trimStartSeconds, _trimEndSeconds);
        _playbackStartSeconds = _currentSeconds;
        _playbackStartedAt = DateTime.Now;
        if (_previewAudioReader is not null)
        {
            _previewAudioReader.CurrentTime = TimeSpan.FromSeconds(_currentSeconds);
        }
    }

    private double? GetPlaybackSkipTarget(double previousSeconds, double currentSeconds)
    {
        foreach (var cut in _cutRanges.OrderBy(cut => cut.Start))
        {
            var start = cut.Start.TotalSeconds;
            var end = cut.End.TotalSeconds;
            if (end <= _trimStartSeconds || start >= _trimEndSeconds)
            {
                continue;
            }

            start = Math.Max(start, _trimStartSeconds);
            end = Math.Min(end, _trimEndSeconds);
            if ((previousSeconds < start && currentSeconds >= start) ||
                (currentSeconds >= start && currentSeconds < end))
            {
                return end;
            }
        }

        return null;
    }

    private TimelineDragTarget PickTimelineTarget(double x)
    {
        var startX = SecondsToX(_trimStartSeconds);
        var endX = SecondsToX(_trimEndSeconds);
        var currentX = SecondsToX(_currentSeconds);
        _dragCutIndex = -1;

        for (var i = 0; i < _cutRanges.Count; i++)
        {
            var cut = _cutRanges[i];
            var cutStartX = SecondsToX(cut.Start.TotalSeconds);
            var cutEndX = SecondsToX(cut.End.TotalSeconds);
            if (Math.Abs(x - cutStartX) <= 10)
            {
                _dragCutIndex = i;
                return TimelineDragTarget.CutStart;
            }

            if (Math.Abs(x - cutEndX) <= 10)
            {
                _dragCutIndex = i;
                return TimelineDragTarget.CutEnd;
            }
        }

        if (Math.Abs(x - startX) <= 12)
        {
            return TimelineDragTarget.Start;
        }

        if (Math.Abs(x - endX) <= 12)
        {
            return TimelineDragTarget.End;
        }

        if (Math.Abs(x - currentX) <= 12)
        {
            return TimelineDragTarget.Playhead;
        }

        return TimelineDragTarget.Playhead;
    }

    private void ApplyTimelineDrag(double x)
    {
        var seconds = XToSeconds(x);
        if (_dragTarget == TimelineDragTarget.Start)
        {
            _trimStartSeconds = Math.Clamp(seconds, 0, Math.Max(0, _trimEndSeconds - 0.033));
            SeekPreview(_trimStartSeconds);
        }
        else if (_dragTarget == TimelineDragTarget.End)
        {
            _trimEndSeconds = Math.Clamp(seconds, Math.Min(_clipDuration.TotalSeconds, _trimStartSeconds + 0.033), _clipDuration.TotalSeconds);
            SeekPreview(_trimEndSeconds);
        }
        else if (_dragTarget == TimelineDragTarget.Playhead)
        {
            SeekPreview(Math.Clamp(seconds, _trimStartSeconds, _trimEndSeconds));
        }
        else if (_dragTarget == TimelineDragTarget.CutStart && _dragCutIndex >= 0 && _dragCutIndex < _cutRanges.Count)
        {
            var cut = _cutRanges[_dragCutIndex];
            var previousEnd = _dragCutIndex > 0 ? _cutRanges[_dragCutIndex - 1].End.TotalSeconds + 0.033 : _trimStartSeconds;
            var start = Math.Clamp(seconds, previousEnd, cut.End.TotalSeconds - 0.033);
            _cutRanges[_dragCutIndex] = cut with { Start = TimeSpan.FromSeconds(start) };
            SeekPreview(start);
        }
        else if (_dragTarget == TimelineDragTarget.CutEnd && _dragCutIndex >= 0 && _dragCutIndex < _cutRanges.Count)
        {
            var cut = _cutRanges[_dragCutIndex];
            var nextStart = _dragCutIndex < _cutRanges.Count - 1 ? _cutRanges[_dragCutIndex + 1].Start.TotalSeconds - 0.033 : _trimEndSeconds;
            var end = Math.Clamp(seconds, cut.Start.TotalSeconds + 0.033, nextStart);
            _cutRanges[_dragCutIndex] = cut with { End = TimeSpan.FromSeconds(end) };
            SeekPreview(end);
        }

        UpdateTrimText();
    }

    private void ToggleCutAtCurrentTime()
    {
        if (_currentClip is null || _clipDuration.TotalSeconds <= 0)
        {
            return;
        }

        var seconds = Math.Clamp(_currentSeconds, _trimStartSeconds, _trimEndSeconds);
        if (_pendingCutStartSeconds is null)
        {
            _pendingCutStartSeconds = seconds;
            CutButton.Content = "End Cut";
            StatusText.Text = $"Cut start set at {FormatTime(seconds)}. Press C again to close it.";
        }
        else
        {
            var start = Math.Min(_pendingCutStartSeconds.Value, seconds);
            var end = Math.Max(_pendingCutStartSeconds.Value, seconds);
            _pendingCutStartSeconds = null;
            CutButton.Content = "Start Cut";
            if (end - start < 0.033)
            {
                StatusText.Text = "Cut ignored: range is too small.";
            }
            else
            {
                _cutRanges.Add(new CutRange(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
                MergeCutRanges();
                StatusText.Text = $"Cut added: {FormatTime(start)} to {FormatTime(end)}.";
            }
        }

        UpdateTrimText();
    }

    private void RemoveCutAtCurrentTime()
    {
        if (_cutRanges.Count == 0)
        {
            return;
        }

        var current = TimeSpan.FromSeconds(_currentSeconds);
        var index = _cutRanges.FindIndex(cut => current >= cut.Start && current <= cut.End);
        if (index < 0)
        {
            index = _cutRanges
                .Select((cut, i) => new
                {
                    Index = i,
                    Distance = Math.Min(Math.Abs((cut.Start - current).TotalSeconds), Math.Abs((cut.End - current).TotalSeconds))
                })
                .OrderBy(item => item.Distance)
                .First().Index;
        }

        var removed = _cutRanges[index];
        _cutRanges.RemoveAt(index);
        StatusText.Text = $"Cut removed: {FormatTime(removed.Start.TotalSeconds)} to {FormatTime(removed.End.TotalSeconds)}.";
        UpdateTrimText();
    }

    private void MergeCutRanges()
    {
        if (_cutRanges.Count <= 1)
        {
            return;
        }

        var ordered = _cutRanges.OrderBy(cut => cut.Start).ToList();
        _cutRanges.Clear();
        foreach (var cut in ordered)
        {
            var start = TimeSpan.FromSeconds(Math.Clamp(cut.Start.TotalSeconds, _trimStartSeconds, _trimEndSeconds));
            var end = TimeSpan.FromSeconds(Math.Clamp(cut.End.TotalSeconds, _trimStartSeconds, _trimEndSeconds));
            if (end <= start)
            {
                continue;
            }

            if (_cutRanges.Count == 0 || start > _cutRanges[^1].End)
            {
                _cutRanges.Add(new CutRange(start, end));
            }
            else
            {
                _cutRanges[^1] = _cutRanges[^1] with { End = Max(_cutRanges[^1].End, end) };
            }
        }
    }

    private double XToSeconds(double x)
    {
        var width = Math.Max(1, TimelineCanvas.ActualWidth);
        return Math.Clamp(x / width * Math.Max(_clipDuration.TotalSeconds, 0.1), 0, Math.Max(_clipDuration.TotalSeconds, 0.1));
    }

    private double SecondsToX(double seconds)
    {
        var duration = Math.Max(_clipDuration.TotalSeconds, 0.1);
        return Math.Max(1, TimelineCanvas.ActualWidth) * Math.Clamp(seconds, 0, duration) / duration;
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCropOverlayFromText();
    }

    private void CropText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingCropText || !IsLoaded)
        {
            return;
        }

        UpdateCropOverlayFromText();
    }

    private void CropOverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_lastRegion.Width <= 0 || _lastRegion.Height <= 0)
        {
            return;
        }

        var point = e.GetPosition(CropOverlayCanvas);
        _cropDragTarget = PickCropTarget(point);
        if (_cropDragTarget == CropDragTarget.None)
        {
            return;
        }

        _cropDragStartPoint = point;
        _cropDragStartRegion = ReadCropRegion();
        CropOverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void CropOverlayCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_cropDragTarget == CropDragTarget.None)
        {
            return;
        }

        var imageBounds = GetImageBounds();
        if (imageBounds.Width <= 0)
        {
            return;
        }

        var point = e.GetPosition(CropOverlayCanvas);
        var scale = imageBounds.Width / _lastRegion.Width;
        var dx = (int)Math.Round((point.X - _cropDragStartPoint.X) / scale);
        var dy = (int)Math.Round((point.Y - _cropDragStartPoint.Y) / scale);
        var crop = _cropDragStartRegion;

        crop = _cropDragTarget switch
        {
            CropDragTarget.Move => new CaptureRegion(crop.X + dx, crop.Y + dy, crop.Width, crop.Height),
            CropDragTarget.TopLeft => new CaptureRegion(crop.X + dx, crop.Y + dy, crop.Width - dx, crop.Height - dy),
            CropDragTarget.TopRight => new CaptureRegion(crop.X, crop.Y + dy, crop.Width + dx, crop.Height - dy),
            CropDragTarget.BottomLeft => new CaptureRegion(crop.X + dx, crop.Y, crop.Width - dx, crop.Height + dy),
            CropDragTarget.BottomRight => new CaptureRegion(crop.X, crop.Y, crop.Width + dx, crop.Height + dy),
            _ => crop
        };

        SetCropRegion(ClampCrop(crop), updateResize: true);
    }

    private void CropOverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _cropDragTarget = CropDragTarget.None;
        CropOverlayCanvas.ReleaseMouseCapture();
    }

    private CropDragTarget PickCropTarget(System.Windows.Point point)
    {
        var rect = GetDisplayedCropRect();
        const double hit = 14;
        if (Distance(point, new System.Windows.Point(rect.Left, rect.Top)) <= hit)
        {
            return CropDragTarget.TopLeft;
        }

        if (Distance(point, new System.Windows.Point(rect.Right, rect.Top)) <= hit)
        {
            return CropDragTarget.TopRight;
        }

        if (Distance(point, new System.Windows.Point(rect.Left, rect.Bottom)) <= hit)
        {
            return CropDragTarget.BottomLeft;
        }

        if (Distance(point, new System.Windows.Point(rect.Right, rect.Bottom)) <= hit)
        {
            return CropDragTarget.BottomRight;
        }

        return rect.Contains(point) ? CropDragTarget.Move : CropDragTarget.None;
    }

    private void UpdateCropOverlayFromText()
    {
        if (CropRect is null || _lastRegion.Width <= 0 || _lastRegion.Height <= 0)
        {
            return;
        }

        UpdateCropOverlay(ReadCropRegion());
    }

    private void UpdateCropOverlay(CaptureRegion crop)
    {
        var rect = GetDisplayedCropRect(crop);
        Canvas.SetLeft(CropRect, rect.Left);
        Canvas.SetTop(CropRect, rect.Top);
        CropRect.Width = Math.Max(1, rect.Width);
        CropRect.Height = Math.Max(1, rect.Height);
        PlaceHandle(CropHandleTopLeft, rect.Left, rect.Top);
        PlaceHandle(CropHandleTopRight, rect.Right, rect.Top);
        PlaceHandle(CropHandleBottomLeft, rect.Left, rect.Bottom);
        PlaceHandle(CropHandleBottomRight, rect.Right, rect.Bottom);
    }

    private void SetCropRegion(CaptureRegion crop, bool updateResize)
    {
        _isUpdatingCropText = true;
        CropXText.Text = crop.X.ToString();
        CropYText.Text = crop.Y.ToString();
        CropWidthText.Text = crop.Width.ToString();
        CropHeightText.Text = crop.Height.ToString();
        if (updateResize)
        {
            SetOutputSize(crop.Width, crop.Height);
        }

        _isUpdatingCropText = false;
        UpdateCropOverlay(crop);
    }

    private CaptureRegion ReadCropRegion()
    {
        return ClampCrop(new CaptureRegion(
            ReadInt(CropXText, 0),
            ReadInt(CropYText, 0),
            ReadInt(CropWidthText, _lastRegion.Width),
            ReadInt(CropHeightText, _lastRegion.Height)));
    }

    private CaptureRegion ClampCrop(CaptureRegion crop)
    {
        var width = Math.Clamp(crop.Width, 8, Math.Max(8, _lastRegion.Width));
        var height = Math.Clamp(crop.Height, 8, Math.Max(8, _lastRegion.Height));
        var x = Math.Clamp(crop.X, 0, Math.Max(0, _lastRegion.Width - width));
        var y = Math.Clamp(crop.Y, 0, Math.Max(0, _lastRegion.Height - height));
        return new CaptureRegion(x, y, width, height);
    }

    private Rect GetDisplayedCropRect() => GetDisplayedCropRect(ReadCropRegion());

    private Rect GetDisplayedCropRect(CaptureRegion crop)
    {
        var imageBounds = GetImageBounds();
        if (imageBounds.Width <= 0 || _lastRegion.Width <= 0)
        {
            return Rect.Empty;
        }

        var scale = imageBounds.Width / _lastRegion.Width;
        return new Rect(
            imageBounds.Left + crop.X * scale,
            imageBounds.Top + crop.Y * scale,
            crop.Width * scale,
            crop.Height * scale);
    }

    private Rect GetImageBounds()
    {
        var hostWidth = PreviewHost.ActualWidth;
        var hostHeight = PreviewHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0 || _lastRegion.Width <= 0 || _lastRegion.Height <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(hostWidth / _lastRegion.Width, hostHeight / _lastRegion.Height);
        var width = _lastRegion.Width * scale;
        var height = _lastRegion.Height * scale;
        return new Rect((hostWidth - width) / 2, (hostHeight - height) / 2, width, height);
    }

    private static void PlaceHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - handle.Width / 2);
        Canvas.SetTop(handle, y - handle.Height / 2);
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss\.fff")
            : value.ToString(@"mm\:ss\.fff");
    }

    private void SetOutputSize(int width, int height)
    {
        _isUpdatingOutputSize = true;
        OutputWidthText.Text = Math.Max(1, width).ToString();
        OutputHeightText.Text = Math.Max(1, height).ToString();
        _isUpdatingOutputSize = false;
        UpdateQualityBudgetText();
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static string FormatBytes(long bytes)
    {
        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }

    private static string ShortError(string message)
    {
        if (message.Contains("No capable devices found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Codec not supported", StringComparison.OrdinalIgnoreCase))
        {
            return "Selected GPU encoder is unavailable on this GPU/driver.";
        }

        var firstLine = message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstLine is null ? "Export failed." : firstLine[..Math.Min(firstLine.Length, 180)];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void LoadSettingsIntoUi()
    {
        _isLoadingSettings = true;
        SaveFolderText.Text = _settings.SaveFolder;
        FfmpegPathText.Text = _settings.FfmpegPath;
        MaxSizeText.Text = _settings.MaxMegabytes.ToString("0.##");
        FrameRateText.Text = _settings.FrameRate.ToString();
        QualityCapCheckBox.IsChecked = _settings.QualityLengthCapEnabled;
        QualityKbpsText.Text = _settings.QualityTargetKbps.ToString();
        IncludeAudioCheckBox.IsChecked = _settings.IncludeAudio;
        UpdateEncoderComboLabels();
        SelectEncoder(_settings.ExportEncoderKey);
        _settings.StartWithWindows = _startupService.IsEnabled();
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        UpdateRepositoryUrlText.Text = _settings.GitHubRepositoryUrl;
        RecordHotKeyText.Text = _settings.RecordHotKey.Label;
        ResetHotKeyText.Text = _settings.ResetHotKey.Label;
        _isLoadingSettings = false;
    }

    private void SaveSettingsFromUi()
    {
        _settings.SaveFolder = string.IsNullOrWhiteSpace(SaveFolderText.Text)
            ? _settings.SaveFolder
            : SaveFolderText.Text.Trim();
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPathText.Text)
            ? "ffmpeg"
            : FfmpegPathText.Text.Trim();
        _settings.MaxMegabytes = ReadDouble(MaxSizeText, 10);
        _settings.FrameRate = Math.Clamp(ReadInt(FrameRateText, 30), 5, 60);
        _settings.QualityLengthCapEnabled = QualityCapCheckBox.IsChecked == true;
        _settings.QualityTargetKbps = Math.Clamp(ReadInt(QualityKbpsText, 10000), 500, 50000);
        _settings.IncludeAudio = IncludeAudioCheckBox.IsChecked == true;
        _settings.ExportEncoderKey = GetSelectedEncoderKey();
        _settings.AudioDeviceName = "Desktop audio";
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _settings.GitHubRepositoryUrl = UpdateRepositoryUrlText.Text.Trim();
        _startupService.SetEnabled(_settings.StartWithWindows);
        _settingsService.Save(_settings);
        ApplyHotKeys();
        UpdateSettingsSummary();
    }

    private void UpdateTrimText()
    {
        if (ClipInfoText is null || PreviewTimeText is null)
        {
            return;
        }

        var start = _trimStartSeconds;
        var end = _trimEndSeconds;
        var current = _currentSeconds;
        var cutText = _cutRanges.Count == 0
            ? ""
            : $" | cuts {_cutRanges.Count}";
        if (_pendingCutStartSeconds is not null)
        {
            cutText += $" | cut start {FormatTime(_pendingCutStartSeconds.Value)}";
        }

        ClipInfoText.Text = _currentClip is null
            ? "No clip loaded."
            : $"{Path.GetFileName(_currentClip)} | {FormatTime(start)} to {FormatTime(end)} | current {FormatTime(current)}{cutText}";
        PreviewTimeText.Text = $"{FormatTime(current)} / {FormatTime(_clipDuration.TotalSeconds)}";
        TrimStartText.Text = $"Start {FormatTime(start)}";
        TrimEndText.Text = $"End {FormatTime(end)}";
        UpdateTimelineVisuals();
    }

    private void UpdateTimelineVisuals()
    {
        if (TrimBand is null || PlayheadMarker is null || StartHandle is null || EndHandle is null || _clipDuration.TotalSeconds <= 0)
        {
            return;
        }

        var startX = SecondsToX(_trimStartSeconds);
        var endX = SecondsToX(_trimEndSeconds);
        var currentX = SecondsToX(_currentSeconds);
        var height = Math.Max(1, TimelineCanvas.ActualHeight);
        TrimBand.Height = height;
        StartHandle.Height = height;
        EndHandle.Height = height;
        PlayheadMarker.Height = height;
        Canvas.SetLeft(TrimBand, startX);
        TrimBand.Width = Math.Max(2, endX - startX);
        Canvas.SetLeft(StartHandle, startX - StartHandle.Width / 2);
        Canvas.SetLeft(EndHandle, endX - EndHandle.Width / 2);
        Canvas.SetLeft(PlayheadMarker, currentX - PlayheadMarker.Width / 2);
        UpdateCutVisuals();
    }

    private void UpdateCutVisuals()
    {
        var existing = TimelineCanvas.Children
            .OfType<FrameworkElement>()
            .Where(element => Equals(element.Tag, "CutVisual"))
            .ToList();
        foreach (var element in existing)
        {
            TimelineCanvas.Children.Remove(element);
        }

        foreach (var cut in _cutRanges)
        {
            var start = Math.Max(_trimStartSeconds, cut.Start.TotalSeconds);
            var end = Math.Min(_trimEndSeconds, cut.End.TotalSeconds);
            if (end <= start)
            {
                continue;
            }

            var startX = SecondsToX(start);
            var endX = SecondsToX(end);
            var band = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, endX - startX),
                Height = Math.Max(1, TimelineCanvas.ActualHeight),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 239, 68, 68)),
                Tag = "CutVisual"
            };
            Canvas.SetLeft(band, startX);
            Canvas.SetTop(band, 0);
            TimelineCanvas.Children.Add(band);

            var startHandle = new System.Windows.Shapes.Rectangle
            {
                Width = 5,
                Height = Math.Max(1, TimelineCanvas.ActualHeight),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28)),
                RadiusX = 2,
                RadiusY = 2,
                Tag = "CutVisual"
            };
            Canvas.SetLeft(startHandle, startX - 2.5);
            Canvas.SetTop(startHandle, 0);
            TimelineCanvas.Children.Add(startHandle);

            var endHandle = new System.Windows.Shapes.Rectangle
            {
                Width = 5,
                Height = Math.Max(1, TimelineCanvas.ActualHeight),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28)),
                RadiusX = 2,
                RadiusY = 2,
                Tag = "CutVisual"
            };
            Canvas.SetLeft(endHandle, endX - 2.5);
            Canvas.SetTop(endHandle, 0);
            TimelineCanvas.Children.Add(endHandle);
        }

        if (_pendingCutStartSeconds is not null)
        {
            var x = SecondsToX(_pendingCutStartSeconds.Value);
            var marker = new System.Windows.Shapes.Rectangle
            {
                Width = 3,
                Height = Math.Max(1, TimelineCanvas.ActualHeight),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)),
                Tag = "CutVisual"
            };
            Canvas.SetLeft(marker, x - 1.5);
            Canvas.SetTop(marker, 0);
            TimelineCanvas.Children.Add(marker);
        }

        StartHandle.SetValue(System.Windows.Controls.Panel.ZIndexProperty, 10);
        EndHandle.SetValue(System.Windows.Controls.Panel.ZIndexProperty, 10);
        PlayheadMarker.SetValue(System.Windows.Controls.Panel.ZIndexProperty, 11);
    }

    private bool ApplyQualityTrimCap()
    {
        if (!_settings.QualityLengthCapEnabled || _clipDuration.TotalSeconds <= 0)
        {
            return false;
        }

        var maxSeconds = CalculateQualityMaxSeconds();
        if (maxSeconds <= 0 || _trimEndSeconds - _trimStartSeconds <= maxSeconds)
        {
            return false;
        }

        _trimEndSeconds = Math.Min(_clipDuration.TotalSeconds, _trimStartSeconds + maxSeconds);
        if (_currentSeconds > _trimEndSeconds)
        {
            _currentSeconds = _trimEndSeconds;
        }

        UpdateTrimText();
        UpdateQualityBudgetText();
        return true;
    }

    private double CalculateQualityMaxSeconds()
    {
        var audioKbps = _settings.IncludeAudio ? 96 : 0;
        var totalKbits = _settings.MaxMegabytes * 8192 * 0.985;
        var totalKbps = CalculateEffectiveQualityKbps() + audioKbps;
        return totalKbps <= 0 ? 0 : totalKbits / totalKbps;
    }

    private int CalculateEffectiveQualityKbps()
    {
        var width = Math.Max(1, ReadInt(OutputWidthText, _lastRegion.Width > 0 ? _lastRegion.Width : 1920));
        var height = Math.Max(1, ReadInt(OutputHeightText, _lastRegion.Height > 0 ? _lastRegion.Height : 1080));
        var pixels = width * height;
        var scale = pixels / (1920.0 * 1080.0);
        return Math.Clamp((int)Math.Round(_settings.QualityTargetKbps * scale), 1200, _settings.QualityTargetKbps);
    }

    private void UpdateQualityBudgetText()
    {
        if (QualityBudgetText is null)
        {
            return;
        }

        var effectiveKbps = CalculateEffectiveQualityKbps();
        var maxSeconds = CalculateQualityMaxSeconds();
        var currentTrim = Math.Max(0, _trimEndSeconds - _trimStartSeconds);
        var mode = _settings.QualityLengthCapEnabled ? "on" : "off";
        QualityBudgetText.Text =
            $"Quality cap {mode}: {effectiveKbps:N0} kbps video at current resize, max {FormatTime(maxSeconds)} for {_settings.MaxMegabytes:0.##} MB. Current trim {FormatTime(currentTrim)}.";
    }

    private void UpdateSettingsSummary()
    {
        if (CurrentSettingsText is null)
        {
            return;
        }

        CurrentSettingsText.Text =
            $"Current: {_settings.FrameRate} FPS, max {_settings.MaxMegabytes:0.##} MB, quality cap {(_settings.QualityLengthCapEnabled ? "on" : "off")}, encoder {_settings.ExportEncoderKey}, audio {(_settings.IncludeAudio ? "on" : "off")}, startup {(_settings.StartWithWindows ? "on" : "off")}, updates {(string.IsNullOrWhiteSpace(_settings.GitHubRepositoryUrl) ? "off" : "on")}, hotkeys {_settings.RecordHotKey.Label}/{_settings.ResetHotKey.Label}, save to {_settings.SaveFolder}";
        UpdateQualityBudgetText();
    }

    private string GetSelectedEncoderKey()
    {
        return (EncoderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "x264-slow";
    }

    private void SelectEncoder(string key)
    {
        foreach (var item in EncoderComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == key && item.Visibility == Visibility.Visible)
            {
                EncoderComboBox.SelectedItem = item;
                return;
            }
        }

        EncoderComboBox.SelectedIndex = 0;
    }

    private void UpdateEncoderComboLabels()
    {
        var fastest = _settings.EncoderBenchmarks
            .Where(result => result.Seconds > 0)
            .OrderBy(result => result.Seconds)
            .FirstOrDefault();
        var fastestSeconds = Math.Max(0.001, fastest?.Seconds ?? 1);

        foreach (var item in EncoderComboBox.Items.OfType<ComboBoxItem>())
        {
            var key = item.Tag?.ToString() ?? "";
            item.Visibility = key != "all-test" && _settings.UnsupportedEncoderKeys.Contains(key)
                ? Visibility.Collapsed
                : Visibility.Visible;

            var label = EncoderLabelForKey(key);
            if (key.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            {
                label += " (GPU)";
                item.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                item.FontWeight = FontWeights.Normal;
            }

            var benchmark = _settings.EncoderBenchmarks.FirstOrDefault(result => result.EncoderKey == key);
            if (benchmark is not null && fastest is not null)
            {
                label += $" - {benchmark.Seconds / fastestSeconds:0.0}x, {FormatBytes(benchmark.Bytes)}";
            }

            item.Content = label;
        }
    }

    private static string EncoderLabelForKey(string key)
    {
        return key switch
        {
            "x264-slow" => "H.264 Slow",
            "x264-medium" => "H.264 Medium",
            "x264-veryslow" => "H.264 Veryslow",
            "x265-medium" => "H.265 Medium",
            "x265-slow" => "H.265 Slow",
            "h264-nvenc-fast" => "H.264 NVENC Fast",
            "h264-nvenc" => "H.264 NVENC Quality",
            "h264-nvenc-max" => "H.264 NVENC Max",
            "hevc-nvenc-fast" => "H.265 NVENC Fast",
            "hevc-nvenc" => "H.265 NVENC Quality",
            "hevc-nvenc-max" => "H.265 NVENC Max",
            "all-test" => "All Test Exports",
            _ => key
        };
    }

    private static int ReadInt(System.Windows.Controls.TextBox textBox, int fallback)
    {
        return int.TryParse(textBox.Text, out var value) ? Math.Max(0, value) : fallback;
    }

    private static double ReadDouble(System.Windows.Controls.TextBox textBox, double fallback)
    {
        return double.TryParse(textBox.Text, out var value) ? Math.Max(0.1, value) : fallback;
    }
}

internal enum TimelineDragTarget
{
    None,
    Start,
    End,
    Playhead,
    CutStart,
    CutEnd
}

internal enum CropDragTarget
{
    None,
    Move,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}
