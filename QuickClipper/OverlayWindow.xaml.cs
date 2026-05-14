using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickClipper;

public partial class OverlayWindow : Window
{
    private readonly TaskCompletionSource<CaptureRegion?> _completion = new();
    private System.Windows.Point _start;
    private bool _dragging;

    public OverlayWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public Task<CaptureRegion?> SelectRegionAsync()
    {
        Show();
        Activate();
        Focus();
        return _completion.Task;
    }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(RootCanvas);
        SelectionRect.Visibility = Visibility.Visible;
        RootCanvas.CaptureMouse();
        UpdateSelection(_start);
    }

    private void RootCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
        {
            UpdateSelection(e.GetPosition(RootCanvas));
        }
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        RootCanvas.ReleaseMouseCapture();
        var end = e.GetPosition(RootCanvas);
        var region = BuildRegion(_start, end);
        _completion.TrySetResult(region.IsValid ? region : null);
        Close();
    }

    private void RootCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelSelection();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelSelection();
        }
    }

    private void CancelSelection()
    {
        if (_dragging)
        {
            _dragging = false;
            RootCanvas.ReleaseMouseCapture();
        }

        _completion.TrySetResult(null);
        Close();
    }

    private void UpdateSelection(System.Windows.Point end)
    {
        var x = Math.Min(_start.X, end.X);
        var y = Math.Min(_start.Y, end.Y);
        var width = Math.Abs(end.X - _start.X);
        var height = Math.Abs(end.Y - _start.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = width;
        SelectionRect.Height = height;
    }

    private CaptureRegion BuildRegion(System.Windows.Point start, System.Windows.Point end)
    {
        var x = (int)Math.Round(Math.Min(start.X, end.X) + Left);
        var y = (int)Math.Round(Math.Min(start.Y, end.Y) + Top);
        var width = (int)Math.Round(Math.Abs(end.X - start.X));
        var height = (int)Math.Round(Math.Abs(end.Y - start.Y));
        return new CaptureRegion(x, y, width, height);
    }
}
