using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Core.AirPlay;

public static class AirPlayReceiverDetector
{
    private static readonly string[] ProcessHints = ["airserver", "reflector", "airplay-windows"];
    private static readonly string[] TitleHints = ["AirServer", "Reflector", "AirPlay"];

    public static bool IsLikelyReceiver(string? processName, string? exePath, string? title)
    {
        return Score(processName, exePath, title) > 0;
    }

    public static IReadOnlyList<ReceiverWindowInfo> FilterReceivers(IEnumerable<ReceiverWindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return windows
            .Select(w =>
            {
                var score = Score(w.ProcessName, w.ExePath, w.Title);
                var likely = score > 0;
                return (Window: w with { IsLikelyAirPlayReceiver = likely }, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Window.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Window)
            .ToList();
    }

    /// <summary>
    /// Higher scores prefer process/exe matches over title-only matches.
    /// </summary>
    public static int Score(string? processName, string? exePath, string? title)
    {
        var score = 0;

        if (ContainsAny(processName, ProcessHints))
        {
            score += 100;
        }

        if (ContainsAny(exePath, ProcessHints))
        {
            score += 80;
        }

        if (ContainsAny(title, TitleHints))
        {
            score += 40;
        }

        return score;
    }

    private static bool ContainsAny(string? value, IEnumerable<string> hints)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var hint in hints)
        {
            if (value.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
