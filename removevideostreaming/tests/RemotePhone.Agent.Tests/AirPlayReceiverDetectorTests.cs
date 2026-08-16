using FluentAssertions;
using RemotePhone.Agent.Core.AirPlay;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Tests;

public class AirPlayReceiverDetectorTests
{
    [Theory]
    [InlineData("AirServer", null, null)]
    [InlineData("airserver", null, null)]
    [InlineData("Reflector", null, null)]
    [InlineData(null, @"C:\Program Files\AirServer\AirServer.exe", null)]
    [InlineData(null, null, "AirServer Universal")]
    [InlineData(null, null, "Reflector 4")]
    [InlineData(null, null, "AirPlay Receiver")]
    [InlineData("airplay-windows", null, null)]
    [InlineData("AirPlay-Windows", null, "AirPlay-Windows")]
    public void IsLikelyReceiver_matches_process_exe_or_title(string? process, string? exe, string? title)
    {
        AirPlayReceiverDetector.IsLikelyReceiver(process, exe, title).Should().BeTrue();
    }

    [Theory]
    [InlineData("notepad", @"C:\Windows\notepad.exe", "Untitled")]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("chrome", null, "YouTube")]
    public void IsLikelyReceiver_rejects_non_receivers(string? process, string? exe, string? title)
    {
        AirPlayReceiverDetector.IsLikelyReceiver(process, exe, title).Should().BeFalse();
    }

    [Fact]
    public void Score_prefers_process_over_title_only()
    {
        var processScore = AirPlayReceiverDetector.Score("AirServer", null, null);
        var titleScore = AirPlayReceiverDetector.Score(null, null, "AirPlay Mirror");

        processScore.Should().BeGreaterThan(titleScore);
        processScore.Should().Be(100);
        titleScore.Should().Be(40);
    }

    [Fact]
    public void FilterReceivers_keeps_scored_windows_ordered_by_score()
    {
        var windows = new[]
        {
            new ReceiverWindowInfo(1, "Untitled - Notepad", "notepad", 10, @"C:\Windows\notepad.exe", 800, 600, false),
            new ReceiverWindowInfo(2, "AirPlay Mirror", "somehost", 20, @"C:\apps\host.exe", 1920, 1080, false),
            new ReceiverWindowInfo(3, "Desktop", "AirServer", 30, @"C:\AirServer\AirServer.exe", 1170, 2532, false),
            new ReceiverWindowInfo(4, "Reflector", "other", 40, @"C:\Reflector\app.exe", 1280, 720, false),
        };

        var filtered = AirPlayReceiverDetector.FilterReceivers(windows);

        filtered.Should().HaveCount(3);
        filtered[0].ProcessName.Should().Be("AirServer");
        filtered[0].IsLikelyAirPlayReceiver.Should().BeTrue();
        filtered.Select(w => w.Hwnd).Should().NotContain(1);
    }
}
