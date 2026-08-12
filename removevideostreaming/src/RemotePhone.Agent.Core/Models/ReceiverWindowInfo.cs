namespace RemotePhone.Agent.Core.Models;

public sealed record ReceiverWindowInfo(
    nint Hwnd,
    string Title,
    string ProcessName,
    int ProcessId,
    string ExePath,
    int Width,
    int Height,
    bool IsLikelyAirPlayReceiver);
