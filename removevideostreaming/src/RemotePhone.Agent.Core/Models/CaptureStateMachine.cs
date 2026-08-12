namespace RemotePhone.Agent.Core.Models;

/// <summary>
/// Enforces legal capture lifecycle transitions and raises <see cref="StateChanged"/> on success.
/// </summary>
public sealed class CaptureStateMachine
{
    private static readonly Dictionary<CaptureState, HashSet<CaptureState>> LegalTransitions = new()
    {
        [CaptureState.Idle] = [CaptureState.Selecting],
        [CaptureState.Selecting] = [CaptureState.Capturing, CaptureState.Idle],
        [CaptureState.Capturing] = [CaptureState.SourceLost, CaptureState.Stopped, CaptureState.Error],
        [CaptureState.SourceLost] = [CaptureState.Capturing, CaptureState.Stopped, CaptureState.Idle],
        [CaptureState.Stopped] = [CaptureState.Idle, CaptureState.Selecting],
        [CaptureState.Error] = [CaptureState.Idle, CaptureState.Selecting],
    };

    private readonly object _gate = new();

    public CaptureState Current { get; private set; } = CaptureState.Idle;

    public event EventHandler<CaptureState>? StateChanged;

    public bool TryTransition(CaptureState next)
    {
        lock (_gate)
        {
            if (!LegalTransitions.TryGetValue(Current, out var allowed) || !allowed.Contains(next))
            {
                return false;
            }

            Current = next;
        }

        StateChanged?.Invoke(this, next);
        return true;
    }
}
