using System.IO;
using NAudio.Wave;

namespace QuickClipper;

public sealed class LoopbackAudioRecorder : IDisposable
{
    private readonly WasapiLoopbackCapture _capture = new();
    private readonly WaveFileWriter _writer;

    public string OutputPath { get; }

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? FirstSampleAtUtc { get; private set; }

    public DateTime EffectiveStartAtUtc => FirstSampleAtUtc ?? StartedAtUtc;

    public LoopbackAudioRecorder()
    {
        OutputPath = Path.Combine(Path.GetTempPath(), $"quickclipper-audio-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        _writer = new WaveFileWriter(OutputPath, _capture.WaveFormat);
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                FirstSampleAtUtc ??= DateTime.UtcNow;
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                _writer.Flush();
            }
        };
    }

    public void Start()
    {
        StartedAtUtc = DateTime.UtcNow;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture.CaptureState == NAudio.CoreAudioApi.CaptureState.Capturing)
        {
            _capture.StopRecording();
        }
    }

    public void Dispose()
    {
        _capture.Dispose();
        _writer.Dispose();
    }
}
