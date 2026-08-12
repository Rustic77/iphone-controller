using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone_Agent.Services;

/// <summary>
/// Structured application log helpers. Never log frame bytes or pixel buffers.
/// </summary>
public static class AgentLogging
{
    public static void ApplicationStarted(ILogger logger, string appVersion, string deviceId, string agentId)
        => logger.LogInformation(
            "ApplicationStarted AppVersion={AppVersion} DeviceId={DeviceId} AgentId={AgentId}",
            appVersion,
            deviceId,
            agentId);

    public static void ReceiverFound(
        ILogger logger,
        string title,
        string processName,
        nint hwnd,
        int width,
        int height)
        => logger.LogInformation(
            "ReceiverFound Title={Title} ProcessName={ProcessName} Hwnd={Hwnd} Width={Width} Height={Height}",
            title,
            processName,
            hwnd,
            width,
            height);

    public static void ReceiverSelected(
        ILogger logger,
        string title,
        string processName,
        nint hwnd)
        => logger.LogInformation(
            "ReceiverSelected Title={Title} ProcessName={ProcessName} Hwnd={Hwnd}",
            title,
            processName,
            hwnd);

    public static void CaptureStarted(ILogger logger, nint hwnd, int width, int height)
        => logger.LogInformation(
            "CaptureStarted Hwnd={Hwnd} Width={Width} Height={Height}",
            hwnd,
            width,
            height);

    public static void CaptureStopped(ILogger logger, string reason)
        => logger.LogInformation("CaptureStopped Reason={Reason}", reason);

    public static void ResolutionChanged(ILogger logger, int width, int height)
        => logger.LogInformation("ResolutionChanged Width={Width} Height={Height}", width, height);

    public static void OrientationChanged(ILogger logger, ScreenOrientation orientation)
        => logger.LogInformation("OrientationChanged Orientation={Orientation}", orientation);

    public static void SourceLost(ILogger logger, string? reason)
        => logger.LogWarning("SourceLost Reason={Reason}", reason ?? "unknown");

    public static void CaptureError(ILogger logger, Exception exception, string context)
        => logger.LogError(exception, "CaptureError Context={Context}", context);
}
