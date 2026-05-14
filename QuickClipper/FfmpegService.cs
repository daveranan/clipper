using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuickClipper;

public sealed class FfmpegService
{
    public string GetSiblingToolPath(AppSettings settings, string toolName)
    {
        if (Path.IsPathFullyQualified(settings.FfmpegPath))
        {
            var directory = Path.GetDirectoryName(settings.FfmpegPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var sibling = Path.Combine(directory, toolName);
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }
        }

        return toolName;
    }

    public async Task<ActiveRecording> StartRecordingAsync(AppSettings settings, CaptureRegion region)
    {
        region = region.NormalizeForEncoder();
        Directory.CreateDirectory(settings.SaveFolder);
        var outputPath = Path.Combine(Path.GetTempPath(), $"quickclipper-{DateTime.Now:yyyyMMdd-HHmmss}.mkv");
        var args = BuildSilentCaptureArgs(settings, region, outputPath);
        var errorLog = new StringBuilder();
        var firstFrame = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = StartProcess(settings.FfmpegPath, args, errorLog, line =>
        {
            if (line.Contains("frame=", StringComparison.OrdinalIgnoreCase))
            {
                firstFrame.TrySetResult(DateTime.UtcNow);
            }
        });
        var audioRecorder = settings.IncludeAudio ? new LoopbackAudioRecorder() : null;
        var readyTask = await Task.WhenAny(firstFrame.Task, Task.Delay(2000));
        var videoStartedAtUtc = readyTask == firstFrame.Task ? await firstFrame.Task : DateTime.UtcNow;

        if (process.HasExited)
        {
            var details = errorLog.Length > 0 ? errorLog.ToString().Trim() : "No FFmpeg error output.";
            throw new InvalidOperationException($"FFmpeg exited immediately: {details}");
        }

        audioRecorder?.Start();
        return new ActiveRecording(process, outputPath, region, errorLog, audioRecorder, settings.FfmpegPath, videoStartedAtUtc);
    }

    public async Task StopRecordingAsync(ActiveRecording recording, bool muxAudio = true)
    {
        if (!recording.Process.HasExited)
        {
            await recording.Process.StandardInput.WriteLineAsync("q");
            if (!recording.Process.WaitForExit(30000))
            {
                recording.Process.Kill(entireProcessTree: true);
                await recording.Process.WaitForExitAsync();
            }
        }

        recording.AudioRecorder?.Stop();
        recording.AudioRecorder?.Dispose();

        if (muxAudio && recording.AudioRecorder is not null && File.Exists(recording.AudioRecorder.OutputPath))
        {
            var audioOffset = recording.AudioRecorder.EffectiveStartAtUtc - recording.VideoStartedAtUtc;
            if (audioOffset < TimeSpan.Zero)
            {
                audioOffset = TimeSpan.Zero;
            }

            await MuxAudioAsync(recording.FfmpegPath, recording.OutputPath, recording.AudioRecorder.OutputPath, audioOffset);
        }
    }

    public async Task FinalizeRecordingAudioAsync(ActiveRecording recording)
    {
        if (recording.AudioRecorder is null || !File.Exists(recording.AudioRecorder.OutputPath))
        {
            return;
        }

        var audioOffset = recording.AudioRecorder.EffectiveStartAtUtc - recording.VideoStartedAtUtc;
        if (audioOffset < TimeSpan.Zero)
        {
            audioOffset = TimeSpan.Zero;
        }

        try
        {
            await MuxAudioAsync(recording.FfmpegPath, recording.OutputPath, recording.AudioRecorder.OutputPath, audioOffset);
        }
        catch
        {
            TryDelete(recording.AudioRecorder.OutputPath);
        }
    }

    public async Task<IReadOnlyList<ExportResult>> ExportAsync(AppSettings settings, ExportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        var encoders = settings.ExportEncoderKey == "all-test"
            ? ExportEncoder.Presets
                .Where(encoder => encoder.Key != "all-test" && !settings.UnsupportedEncoderKeys.Contains(encoder.Key))
                .ToList()
            : ExportEncoder.Presets.Where(encoder => encoder.Key == settings.ExportEncoderKey).DefaultIfEmpty(ExportEncoder.Default).ToList();
        var results = new List<ExportResult>();
        foreach (var encoder in encoders)
        {
            var outputPath = encoders.Count == 1
                ? options.OutputPath
                : Path.Combine(
                    Path.GetDirectoryName(options.OutputPath)!,
                    $"{Path.GetFileNameWithoutExtension(options.OutputPath)}-{encoder.Key}{Path.GetExtension(options.OutputPath)}");
            var encoderOptions = options with { OutputPath = outputPath };
            try
            {
                var stopwatch = Stopwatch.StartNew();
                await ExportWithEncoderAsync(settings, encoderOptions, encoder);
                stopwatch.Stop();
                var bitrate = await ProbeBitrateAsync(settings, outputPath);
                results.Add(new ExportResult(encoder.Key, outputPath, encoder.Label, new FileInfo(outputPath).Length, stopwatch.Elapsed, options.Duration, bitrate.VideoKbps, bitrate.AudioKbps, true, "", ""));
            }
            catch (Exception ex)
            {
                results.Add(new ExportResult(encoder.Key, outputPath, encoder.Label, 0, TimeSpan.Zero, options.Duration, 0, 0, false, ex.Message, ""));
            }
        }

        if (results.All(result => !result.Success))
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, results.Select(result => $"{result.EncoderLabel}: {result.Error}")));
        }

        return results;
    }

    private async Task ExportWithEncoderAsync(AppSettings settings, ExportOptions options, ExportEncoder encoder)
    {
        var filters = BuildFilters(options);
        var audioKbps = 96;
        var bitrate = (int)(CalculateVideoBitrateKbps(settings.MaxMegabytes, options.Duration, audioKbps) *
            Math.Clamp(settings.ExportBitrateScale, 0.5, 1.5));
        bitrate = Math.Clamp(bitrate, 250, 12000);
        await ExportOnceAsync(settings, options, encoder, filters, bitrate, audioKbps);
        var targetBytes = settings.MaxMegabytes * 1024 * 1024;
        var actualBytes = (double)new FileInfo(options.OutputPath).Length;

        for (var attempt = 0; attempt < 5 && (actualBytes > targetBytes || actualBytes < targetBytes * 0.95); attempt++)
        {
            var ratio = targetBytes / Math.Max(actualBytes, 1);
            var safety = actualBytes > targetBytes ? 0.98 : 0.99;
            bitrate = Math.Clamp((int)(bitrate * ratio * safety), 250, 12000);
            await ExportOnceAsync(settings, options, encoder, filters, bitrate, audioKbps);
            actualBytes = new FileInfo(options.OutputPath).Length;
            if (actualBytes <= targetBytes && actualBytes >= targetBytes * 0.95)
            {
                break;
            }
        }

        var idealBitrate = CalculateVideoBitrateKbps(settings.MaxMegabytes, options.Duration, audioKbps);
        settings.ExportBitrateScale = Math.Clamp((double)bitrate / Math.Max(idealBitrate, 1), 0.5, 1.5);
    }

    private async Task ExportOnceAsync(
        AppSettings settings,
        ExportOptions options,
        ExportEncoder encoder,
        string filters,
        int videoBitrateKbps,
        int audioBitrateKbps)
    {
        var hasCuts = options.CutRanges.Count > 0;
        var hasAudio = hasCuts && await HasAudioStreamAsync(settings, options.InputPath);
        if (hasCuts)
        {
            await ExportCutOnceAsync(settings, options, encoder, videoBitrateKbps, audioBitrateKbps, hasAudio);
            return;
        }

        var args = string.Join(" ", new[]
        {
            "-hide_banner -y",
            $"-i {Quote(options.InputPath)}",
            $"-ss {Seconds(options.Start)}",
            $"-t {Seconds(options.Duration)}",
            filters.Length > 0 ? $"-vf {Quote(filters)}" : "",
            $"-map 0:v:0 -map 0:a? -r 30 -c:a aac -b:a {audioBitrateKbps}k",
            encoder.VideoArgs,
            $"-b:v {videoBitrateKbps}k -maxrate {videoBitrateKbps}k -bufsize {videoBitrateKbps * 2}k",
            "-movflags +faststart -pix_fmt yuv420p",
            Quote(options.OutputPath)
        });

        var errorLog = new StringBuilder();
        var process = StartProcess(settings.FfmpegPath, args, errorLog);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg export failed: {errorLog}");
        }
    }

    private async Task ExportCutOnceAsync(
        AppSettings settings,
        ExportOptions options,
        ExportEncoder encoder,
        int videoBitrateKbps,
        int audioBitrateKbps,
        bool hasAudio)
    {
        var keptSegments = BuildKeptSegments(options).ToList();
        if (keptSegments.Count == 0)
        {
            throw new InvalidOperationException("All timeline segments are cut.");
        }

        var videoFilters = BuildFilters(options);
        var filterParts = new List<string>();
        for (var i = 0; i < keptSegments.Count; i++)
        {
            var segment = keptSegments[i];
            var videoChain = $"[0:v]trim=start={Seconds(segment.Start)}:end={Seconds(segment.End)},setpts=PTS-STARTPTS";
            if (videoFilters.Length > 0)
            {
                videoChain += "," + videoFilters;
            }

            filterParts.Add(videoChain + $"[v{i}]");
            if (hasAudio)
            {
                filterParts.Add($"[0:a]atrim=start={Seconds(segment.Start)}:end={Seconds(segment.End)},asetpts=PTS-STARTPTS[a{i}]");
            }
        }

        var concatInputs = string.Concat(Enumerable.Range(0, keptSegments.Count).Select(i => hasAudio ? $"[v{i}][a{i}]" : $"[v{i}]"));
        filterParts.Add(hasAudio
            ? $"{concatInputs}concat=n={keptSegments.Count}:v=1:a=1[outv][outa]"
            : $"{concatInputs}concat=n={keptSegments.Count}:v=1:a=0[outv]");

        var args = string.Join(" ", new[]
        {
            "-hide_banner -y",
            $"-i {Quote(options.InputPath)}",
            $"-filter_complex {Quote(string.Join(";", filterParts))}",
            hasAudio ? "-map [outv] -map [outa]" : "-map [outv]",
            hasAudio ? $"-c:a aac -b:a {audioBitrateKbps}k" : "-an",
            "-r 30",
            encoder.VideoArgs,
            $"-b:v {videoBitrateKbps}k -maxrate {videoBitrateKbps}k -bufsize {videoBitrateKbps * 2}k",
            "-movflags +faststart -pix_fmt yuv420p",
            Quote(options.OutputPath)
        });

        var errorLog = new StringBuilder();
        var process = StartProcess(settings.FfmpegPath, args, errorLog);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg export failed: {errorLog}");
        }
    }

    public async Task ExtractFrameAsync(AppSettings settings, string inputPath, TimeSpan position, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var seek = position.TotalMilliseconds <= 1 ? "" : $"-ss {Seconds(position)}";
        var args = string.Join(" ", new[]
        {
            "-hide_banner -y",
            $"-i {Quote(inputPath)}",
            seek,
            "-frames:v 1 -update 1 -q:v 2",
            Quote(outputPath)
        });

        var errorLog = new StringBuilder();
        var process = StartProcess(settings.FfmpegPath, args, errorLog);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Frame preview failed: {errorLog}");
        }
    }

    public async Task ExtractPreviewFramesAsync(AppSettings settings, string inputPath, string outputFolder, int fps)
    {
        Directory.CreateDirectory(outputFolder);
        var outputPattern = Path.Combine(outputFolder, "frame_%06d.jpg");
        var args = string.Join(" ", new[]
        {
            "-hide_banner -y",
            $"-i {Quote(inputPath)}",
            $"-vf {Quote($"fps={fps},scale='min(960,iw)':-2")}",
            "-q:v 6",
            Quote(outputPattern)
        });

        var errorLog = new StringBuilder();
        var process = StartProcess(settings.FfmpegPath, args, errorLog);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Preview cache failed: {errorLog}");
        }
    }

    public async Task<TimeSpan> GetDurationAsync(AppSettings settings, string inputPath)
    {
        var ffprobe = GetSiblingToolPath(settings, "ffprobe.exe");
        var seconds = await ProbeDoubleAsync(ffprobe,
            $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {Quote(inputPath)}");
        if (seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        seconds = await ProbeDoubleAsync(ffprobe,
            $"-v error -select_streams v:0 -show_entries stream=duration -of default=noprint_wrappers=1:nokey=1 {Quote(inputPath)}");
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    public async Task<bool> HasVideoStreamAsync(AppSettings settings, string inputPath)
    {
        var ffprobe = GetSiblingToolPath(settings, "ffprobe.exe");
        return await HasVideoStreamAsync(ffprobe, inputPath);
    }

    private async Task<bool> HasAudioStreamAsync(AppSettings settings, string inputPath)
    {
        var ffprobe = GetSiblingToolPath(settings, "ffprobe.exe");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams a:0 -show_entries stream=codec_type -of default=noprint_wrappers=1:nokey=1 {Quote(inputPath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return false;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Contains("audio", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasVideoStreamAsync(string ffprobe, string inputPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_type -of default=noprint_wrappers=1:nokey=1 {Quote(inputPath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return false;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<BitrateInfo> ProbeBitrateAsync(AppSettings settings, string inputPath)
    {
        var ffprobe = GetSiblingToolPath(settings, "ffprobe.exe");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 {Quote(inputPath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var videoOutput = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams a:0 -show_entries stream=bit_rate -of default=noprint_wrappers=1:nokey=1 {Quote(inputPath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var audioOutput = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new BitrateInfo(ParseKbps(videoOutput), ParseKbps(audioOutput));
    }

    public async Task ExtractAudioPreviewAsync(AppSettings settings, string inputPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var args = string.Join(" ", new[]
        {
            "-hide_banner -y",
            $"-i {Quote(inputPath)}",
            "-vn -acodec pcm_s16le -ar 48000 -ac 2",
            Quote(outputPath)
        });

        var errorLog = new StringBuilder();
        var process = StartProcess(settings.FfmpegPath, args, errorLog);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
        }
    }

    private static async Task MuxAudioAsync(string ffmpegPath, string videoPath, string audioPath, TimeSpan audioOffset)
    {
        var muxedPath = Path.Combine(Path.GetTempPath(), $"quickclipper-muxed-{DateTime.Now:yyyyMMdd-HHmmss}.mkv");
        var audioInput = audioOffset.TotalSeconds > 0.001
            ? $"-itsoffset {Seconds(audioOffset)} -i {Quote(audioPath)}"
            : $"-ss {Seconds(TimeSpan.FromSeconds(Math.Abs(audioOffset.TotalSeconds)))} -i {Quote(audioPath)}";
        var args = string.Join(" ", new[]
        {
            "-hide_banner -y",
            $"-i {Quote(videoPath)}",
            audioInput,
            "-map 0:v:0 -map 1:a:0",
            "-c:v copy -c:a aac -b:a 128k -shortest",
            "-avoid_negative_ts make_zero",
            Quote(muxedPath)
        });

        var errorLog = new StringBuilder();
        var process = StartProcess(ffmpegPath, args, errorLog);
        await process.WaitForExitAsync();
        var ffprobePath = GetSiblingToolPath(ffmpegPath, "ffprobe.exe");
        if (process.ExitCode == 0 &&
            File.Exists(muxedPath) &&
            new FileInfo(muxedPath).Length > 4096 &&
            await HasVideoStreamAsync(ffprobePath, muxedPath))
        {
            File.Copy(muxedPath, videoPath, overwrite: true);
            TryDelete(muxedPath);
            TryDelete(audioPath);
        }
        else if (process.ExitCode == 0)
        {
            TryDelete(muxedPath);
            TryDelete(audioPath);
        }
        else
        {
            throw new InvalidOperationException($"Audio mux failed: {errorLog}");
        }
    }

    private static string GetSiblingToolPath(string ffmpegPath, string toolName)
    {
        if (Path.IsPathFullyQualified(ffmpegPath))
        {
            var directory = Path.GetDirectoryName(ffmpegPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var sibling = Path.Combine(directory, toolName);
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }
        }

        return toolName;
    }

    private static Process StartProcess(string ffmpegPath, string args, StringBuilder errorLog, Action<string>? onErrorLine = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start FFmpeg.");
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                errorLog.AppendLine(e.Data);
                onErrorLine?.Invoke(e.Data);
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static string BuildSilentCaptureArgs(AppSettings settings, CaptureRegion region, string outputPath)
    {
        return string.Join(" ", new[]
        {
            "-hide_banner -stats_period 0.1 -y",
            "-f gdigrab",
            $"-framerate {settings.FrameRate}",
            $"-offset_x {region.X}",
            $"-offset_y {region.Y}",
            $"-video_size {region.Width}x{region.Height}",
            "-i desktop",
            "-an -c:v libx264rgb -preset ultrafast -crf 0 -tune zerolatency",
            Quote(outputPath)
        });
    }

    private static int CalculateVideoBitrateKbps(double maxMegabytes, TimeSpan duration, int audioKbps)
    {
        var seconds = Math.Max(duration.TotalSeconds, 0.5);
        var totalKilobits = maxMegabytes * 8192 * 0.985;
        return Math.Clamp((int)(totalKilobits / seconds) - audioKbps, 250, 12000);
    }

    private static string BuildFilters(ExportOptions options)
    {
        var filters = new List<string>();
        if (options.CropWidth > 0 && options.CropHeight > 0)
        {
            filters.Add($"crop={options.CropWidth}:{options.CropHeight}:{options.CropX}:{options.CropY}");
        }

        if (options.AutoFit720)
        {
            filters.Add("crop='min(iw,ih*16/9)':'min(ih,iw*9/16)':(iw-ow)/2:(ih-oh)/2");
            filters.Add("scale=1280:720");
        }
        else if (options.OutputWidth > 0 && options.OutputHeight > 0)
        {
            filters.Add($"scale={options.OutputWidth}:{options.OutputHeight}");
        }

        return string.Join(",", filters);
    }

    private static IEnumerable<(TimeSpan Start, TimeSpan End)> BuildKeptSegments(ExportOptions options)
    {
        var cursor = options.Start;
        var trimEnd = options.End;
        foreach (var cut in options.CutRanges.OrderBy(cut => cut.Start))
        {
            var cutStart = Max(cut.Start, options.Start);
            var cutEnd = Min(cut.End, trimEnd);
            if (cutEnd <= cutStart)
            {
                continue;
            }

            if (cutStart > cursor)
            {
                yield return (cursor, cutStart);
            }

            cursor = Max(cursor, cutEnd);
        }

        if (cursor < trimEnd)
        {
            yield return (cursor, trimEnd);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<double> ProbeDoubleAsync(string ffprobe, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return 0;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return double.TryParse(output.Trim().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static int ParseKbps(string value)
    {
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps)
            ? bps / 1000
            : 0;
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
}

public sealed record ActiveRecording(
    Process Process,
    string OutputPath,
    CaptureRegion Region,
    StringBuilder ErrorLog,
    LoopbackAudioRecorder? AudioRecorder,
    string FfmpegPath,
    DateTime VideoStartedAtUtc);

public sealed record ExportOptions(
    string InputPath,
    string OutputPath,
    TimeSpan Start,
    TimeSpan End,
    TimeSpan Duration,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    int OutputWidth,
    int OutputHeight,
    bool AutoFit720,
    IReadOnlyList<CutRange> CutRanges);

public sealed record CutRange(TimeSpan Start, TimeSpan End);

public sealed record ExportResult(
    string EncoderKey,
    string Path,
    string EncoderLabel,
    long Bytes,
    TimeSpan Duration,
    TimeSpan MediaDuration,
    int VideoKbps,
    int AudioKbps,
    bool Success,
    string Error,
    string PreviewFramePath);

public sealed record BitrateInfo(int VideoKbps, int AudioKbps);

public sealed record ExportEncoder(string Key, string Label, string VideoArgs)
{
    public static ExportEncoder Default { get; } = new("x264-medium", "H.264 Medium", "-c:v libx264 -preset medium");

    public static IReadOnlyList<ExportEncoder> Presets { get; } = new[]
    {
        Default,
        new ExportEncoder("x264-slow", "H.264 Slow", "-c:v libx264 -preset slow"),
        new ExportEncoder("x264-veryslow", "H.264 Veryslow", "-c:v libx264 -preset veryslow"),
        new ExportEncoder("x265-medium", "H.265 Medium", "-c:v libx265 -preset medium"),
        new ExportEncoder("x265-slow", "H.265 Slow", "-c:v libx265 -preset slow"),
        new ExportEncoder("h264-nvenc-fast", "H.264 NVENC Fast", "-c:v h264_nvenc -preset p1"),
        new ExportEncoder("h264-nvenc", "H.264 NVENC Quality", "-c:v h264_nvenc -preset p5"),
        new ExportEncoder("h264-nvenc-max", "H.264 NVENC Max", "-c:v h264_nvenc -preset p7"),
        new ExportEncoder("hevc-nvenc-fast", "H.265 NVENC Fast", "-c:v hevc_nvenc -preset p1"),
        new ExportEncoder("hevc-nvenc", "H.265 NVENC Quality", "-c:v hevc_nvenc -preset p5"),
        new ExportEncoder("hevc-nvenc-max", "H.265 NVENC Max", "-c:v hevc_nvenc -preset p7"),
        new ExportEncoder("all-test", "All Test", "-c:v libx264 -preset slow")
    };
}
