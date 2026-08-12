namespace RemotePhone.Agent.Core.Reliability;

public sealed class ExponentialBackoff
{
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly double _jitterRatio;
    private readonly Random _random;
    private int _attempt;

    public ExponentialBackoff(
        int minDelayMs = 250,
        int maxDelayMs = 30_000,
        double jitterRatio = 0.2,
        Random? random = null)
    {
        if (minDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minDelayMs));
        }

        if (maxDelayMs < minDelayMs)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelayMs), "maxDelayMs must be >= minDelayMs.");
        }

        if (jitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), "jitterRatio must be in [0, 1].");
        }

        _minDelayMs = minDelayMs;
        _maxDelayMs = maxDelayMs;
        _jitterRatio = jitterRatio;
        _random = random ?? Random.Shared;
    }

    public int Attempt => _attempt;

    public int NextDelayMs()
    {
        var exp = Math.Min(_maxDelayMs, _minDelayMs * Math.Pow(2, _attempt));
        _attempt = checked(_attempt + 1);

        if (_jitterRatio <= 0)
        {
            return (int)Math.Round(Math.Clamp(exp, _minDelayMs, _maxDelayMs));
        }

        var jitterSpan = exp * _jitterRatio;
        var delay = exp + ((_random.NextDouble() * 2.0 - 1.0) * jitterSpan);
        return (int)Math.Round(Math.Clamp(delay, _minDelayMs, _maxDelayMs));
    }

    public void Reset()
    {
        _attempt = 0;
    }
}
