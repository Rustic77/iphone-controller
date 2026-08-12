namespace RemotePhone.Agent.Core.Models;

public sealed record VideoMetadata(
    int Width,
    int Height,
    ScreenOrientation Orientation,
    double Fps);
