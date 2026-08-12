using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Core.Calibration;

public enum CalibrationState
{
    UNCALIBRATED,
    CALIBRATING,
    READY,
    INVALID,
}

public sealed class PointerCalibrationModel
{
    public ScreenOrientation Orientation { get; set; } = ScreenOrientation.Portrait;
    public double AspectRatio { get; set; } = 1.0;
    public double ScaleX { get; set; } = 1.0;
    public double ScaleY { get; set; } = 1.0;
    public int Version { get; set; }
    public CalibrationState State { get; private set; } = CalibrationState.UNCALIBRATED;

    public void Invalidate()
    {
        State = CalibrationState.INVALID;
        Version++;
    }

    public void BeginCalibrate()
    {
        State = CalibrationState.CALIBRATING;
        Version++;
    }

    public void MarkReady()
    {
        if (State is not (CalibrationState.CALIBRATING or CalibrationState.READY))
        {
            throw new InvalidOperationException($"Cannot mark ready from state {State}.");
        }

        State = CalibrationState.READY;
        Version++;
    }

    /// <summary>
    /// Maps normalized coordinates (0..1) to estimated screen pixels using calibrated scales.
    /// </summary>
    public (int X, int Y) NormalizedToEstimatedPixel(double nx, double ny, int screenW, int screenH)
    {
        if (screenW <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenW));
        }

        if (screenH <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenH));
        }

        nx = Math.Clamp(nx, 0.0, 1.0);
        ny = Math.Clamp(ny, 0.0, 1.0);

        var x = (int)Math.Round(nx * screenW * ScaleX);
        var y = (int)Math.Round(ny * screenH * ScaleY);
        x = Math.Clamp(x, 0, screenW - 1);
        y = Math.Clamp(y, 0, screenH - 1);
        return (x, y);
    }
}

public static class HomingPlan
{
    /// <summary>
    /// Builds many relative moves toward the upper-left corner to establish a known cursor origin.
    /// </summary>
    public static IReadOnlyList<(int dx, int dy)> BuildHomingSteps(int maxStep = 50, int sweeps = 40)
    {
        if (maxStep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStep));
        }

        if (sweeps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sweeps));
        }

        var steps = new List<(int dx, int dy)>(sweeps);
        for (var i = 0; i < sweeps; i++)
        {
            steps.Add((-maxStep, -maxStep));
        }

        return steps;
    }
}

public static class MovePlan
{
    public static IReadOnlyList<(int dx, int dy)> BuildRelativeMoves(
        int fromX,
        int fromY,
        int toX,
        int toY,
        int maxStep = 40)
    {
        if (maxStep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStep));
        }

        var dxTotal = toX - fromX;
        var dyTotal = toY - fromY;
        if (dxTotal == 0 && dyTotal == 0)
        {
            return Array.Empty<(int dx, int dy)>();
        }

        var distance = Math.Sqrt((dxTotal * dxTotal) + (dyTotal * dyTotal));
        var stepCount = Math.Max(1, (int)Math.Ceiling(distance / maxStep));
        var steps = new List<(int dx, int dy)>(stepCount);

        var remainingX = dxTotal;
        var remainingY = dyTotal;
        for (var i = 0; i < stepCount; i++)
        {
            var stepsLeft = stepCount - i;
            var dx = (int)Math.Round(remainingX / (double)stepsLeft);
            var dy = (int)Math.Round(remainingY / (double)stepsLeft);
            dx = Math.Clamp(dx, -maxStep, maxStep);
            dy = Math.Clamp(dy, -maxStep, maxStep);
            steps.Add((dx, dy));
            remainingX -= dx;
            remainingY -= dy;
        }

        if (remainingX != 0 || remainingY != 0)
        {
            steps.Add((remainingX, remainingY));
        }

        return steps;
    }
}
