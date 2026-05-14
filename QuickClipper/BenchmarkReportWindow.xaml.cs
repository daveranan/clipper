using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace QuickClipper;

public partial class BenchmarkReportWindow : Window
{
    public BenchmarkReportWindow(IReadOnlyList<ExportResult> results)
    {
        InitializeComponent();
        var successes = results.Where(result => result.Success).ToList();
        var failures = results.Count - successes.Count;
        var fastest = successes.OrderBy(result => result.Duration).FirstOrDefault();
        var smallest = successes.OrderBy(result => result.Bytes).FirstOrDefault();
        var bestTargetUse = successes.OrderByDescending(result => result.Bytes).FirstOrDefault();
        SummaryText.Text = successes.Count == 0
            ? "No encoders completed successfully."
            : $"Fastest: {fastest!.EncoderLabel}. Smallest: {smallest!.EncoderLabel}. Largest under target: {bestTargetUse!.EncoderLabel}. Removed unsupported: {failures}.";

        var fastestSeconds = Math.Max(0.001, fastest?.Duration.TotalSeconds ?? 1);
        ResultsGrid.ItemsSource = successes.Select(result => new BenchmarkRow
        {
            Encoder = result.EncoderLabel,
            Size = $"{result.Bytes / 1024.0 / 1024.0:0.##} MB",
            VideoBitrate = result.VideoKbps > 0 ? $"{result.VideoKbps / 1000.0:0.##} Mbps" : "-",
            AudioBitrate = result.AudioKbps > 0 ? $"{result.AudioKbps} kbps" : "-",
            TotalBitrate = $"{result.Bytes * 8.0 / Math.Max(result.MediaDuration.TotalSeconds, 0.001) / 1_000_000.0:0.##} Mbps",
            Time = $"{result.Duration.TotalSeconds:0.0}s",
            Speed = $"{result.Duration.TotalSeconds / fastestSeconds:0.0}x fastest",
            PreviewFramePath = result.PreviewFramePath
        }).ToList();

        ResultsGrid.SelectedIndex = 0;
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = ResultsGrid.SelectedItem as BenchmarkRow;
        if (row is null || string.IsNullOrWhiteSpace(row.PreviewFramePath) || !File.Exists(row.PreviewFramePath))
        {
            FramePreviewImage.Source = null;
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(row.PreviewFramePath);
        bitmap.EndInit();
        bitmap.Freeze();
        FramePreviewImage.Source = bitmap;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class BenchmarkRow
    {
        public string Encoder { get; set; } = "";

        public string Size { get; set; } = "";

        public string VideoBitrate { get; set; } = "";

        public string AudioBitrate { get; set; } = "";

        public string TotalBitrate { get; set; } = "";

        public string Time { get; set; } = "";

        public string Speed { get; set; } = "";

        public string PreviewFramePath { get; set; } = "";
    }
}
