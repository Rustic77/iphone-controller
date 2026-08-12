namespace RemotePhone.Agent.Core.Reliability;

public sealed class SoakMetrics
{
    public TimeSpan Runtime { get; set; }
    public double CaptureFps { get; set; }
    public double StreamFps { get; set; }
    public long DroppedFrames { get; set; }
    public int ReconnectCount { get; set; }
    public int SourceReconnectCount { get; set; }
    public int ErrorCount { get; set; }
    public long MemoryBytes { get; set; }
}
