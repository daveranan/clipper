namespace QuickClipper;

public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 8 && Height > 8;

    public CaptureRegion NormalizeForEncoder()
    {
        var width = Width % 2 == 0 ? Width : Width - 1;
        var height = Height % 2 == 0 ? Height : Height - 1;
        return new CaptureRegion(X, Y, Math.Max(2, width), Math.Max(2, height));
    }
}
