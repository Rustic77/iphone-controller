using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.AirPlay;
using RemotePhone.Agent.Core.Models;
using RemotePhone_Agent.Services;

namespace RemotePhone_Agent.AirPlay;

/// <summary>
/// Discovers AirPlay-like receiver windows via enumeration + Core detector filters.
/// </summary>
public sealed class AirPlayWindowService
{
    private readonly WindowEnumerator _enumerator;
    private readonly ILogger _logger;

    public AirPlayWindowService(ILogger logger, WindowEnumerator? enumerator = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enumerator = enumerator ?? new WindowEnumerator();
    }

    public IReadOnlyList<ReceiverWindowInfo> Refresh()
    {
        var windows = _enumerator.Enumerate();
        var receivers = AirPlayReceiverDetector.FilterReceivers(windows);

        foreach (var receiver in receivers)
        {
            AgentLogging.ReceiverFound(
                _logger,
                receiver.Title,
                receiver.ProcessName,
                receiver.Hwnd,
                receiver.Width,
                receiver.Height);
        }

        _logger.LogInformation(
            "AirPlay window refresh completed. Candidates={CandidateCount} Filtered={FilteredCount}",
            windows.Count,
            receivers.Count);

        return receivers;
    }
}
