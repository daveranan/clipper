using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuickClipper;

public partial class RecordingOverlayWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _startedAt;

    public RecordingOverlayWindow(CaptureRegion region)
    {
        InitializeComponent();
        Left = region.X - 3;
        Top = region.Y - 36;
        Width = region.Width + 6;
        Height = region.Height + 42;
        CaptureBorder.Width = region.Width + 6;
        CaptureBorder.Height = region.Height + 6;
        System.Windows.Controls.Canvas.SetLeft(CaptureBorder, 0);
        System.Windows.Controls.Canvas.SetTop(CaptureBorder, 33);
        _timer.Tick += (_, _) => UpdateTimer();
    }

    public void Start()
    {
        _startedAt = DateTime.Now;
        Show();
        MakeClickThrough();
        _timer.Start();
        UpdateTimer();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void UpdateTimer()
    {
        var elapsed = DateTime.Now - _startedAt;
        TimerText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, style | 0x20 | 0x80000);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
