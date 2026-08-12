namespace RemotePhone.Agent.Core.Models;

public sealed class CaptureStats
{
    public CaptureState Status { get; set; } = CaptureState.Idle;
    public int Width { get; set; }
    public int Height { get; set; }
    public ScreenOrientation Orientation { get; set; } = ScreenOrientation.Portrait;
    public double Fps { get; set; }
    public long DroppedFrames { get; set; }
    public string? LastError { get; set; }
}
