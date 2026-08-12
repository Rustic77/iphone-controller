namespace RemotePhone.Agent.Core.Models;

public enum CaptureState
{
    Idle,
    Selecting,
    Capturing,
    SourceLost,
    Stopped,
    Error,
}
