namespace RemotePhone.Agent.Core.Calibration;

public sealed record TapNormalized
{
    public double X { get; }
    public double Y { get; }

    public TapNormalized(double x, double y)
    {
        if (x is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "Normalized X must be in [0, 1].");
        }

        if (y is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "Normalized Y must be in [0, 1].");
        }

        X = x;
        Y = y;
    }
}

public static class LetterboxMapper
{
    /// <summary>
    /// Converts a click in the view into normalized content coordinates, correcting letterbox/pillarbox bars.
    /// </summary>
    public static (double nx, double ny) NormalizedFromClick(
        double clickX,
        double clickY,
        double viewW,
        double viewH,
        double contentAspect)
    {
        if (viewW <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewW));
        }

        if (viewH <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewH));
        }

        if (contentAspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentAspect));
        }

        var viewAspect = viewW / viewH;
        double contentW;
        double contentH;
        double offsetX;
        double offsetY;

        if (viewAspect > contentAspect)
        {
            // Pillarbox: content height fills view, bars on left/right.
            contentH = viewH;
            contentW = viewH * contentAspect;
            offsetX = (viewW - contentW) / 2.0;
            offsetY = 0;
        }
        else
        {
            // Letterbox: content width fills view, bars on top/bottom.
            contentW = viewW;
            contentH = viewW / contentAspect;
            offsetX = 0;
            offsetY = (viewH - contentH) / 2.0;
        }

        var localX = clickX - offsetX;
        var localY = clickY - offsetY;
        var nx = Math.Clamp(localX / contentW, 0.0, 1.0);
        var ny = Math.Clamp(localY / contentH, 0.0, 1.0);
        return (nx, ny);
    }
}
