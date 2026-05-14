using System.IO;
using System.Reflection;

namespace QuickClipper;

public sealed class AppSettings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "QuickClipper");

    public string FfmpegPath { get; set; } = "ffmpeg";

    public int FrameRate { get; set; } = 30;

    public double MaxMegabytes { get; set; } = 9.8;

    public bool QualityLengthCapEnabled { get; set; } = true;

    public int QualityTargetKbps { get; set; } = 10000;

    public double ExportBitrateScale { get; set; } = 1.0;

    public string ExportEncoderKey { get; set; } = "x264-medium";

    public List<EncoderBenchmark> EncoderBenchmarks { get; set; } = new();

    public List<string> UnsupportedEncoderKeys { get; set; } = new();

    public bool IncludeAudio { get; set; } = true;

    public int AudioSyncOffsetMs { get; set; }

    public string AudioDeviceName { get; set; } = "Desktop audio";

    public bool StartWithWindows { get; set; }

    public string GitHubRepositoryUrl { get; set; } = DefaultGitHubRepositoryUrl();

    public HotKeyBinding RecordHotKey { get; set; } = HotKeyBinding.DefaultRecord();

    public HotKeyBinding ResetHotKey { get; set; } = HotKeyBinding.DefaultReset();

    public string HotKeyLabel { get; set; } = "Win+Shift+R";

    private static string DefaultGitHubRepositoryUrl() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "GitHubRepositoryUrl")
            ?.Value ?? "";
}

public sealed class EncoderBenchmark
{
    public string EncoderKey { get; set; } = "";

    public string EncoderLabel { get; set; } = "";

    public double Seconds { get; set; }

    public long Bytes { get; set; }

    public DateTime TestedAt { get; set; }
}
