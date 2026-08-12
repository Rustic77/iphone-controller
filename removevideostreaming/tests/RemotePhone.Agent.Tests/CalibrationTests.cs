using FluentAssertions;
using RemotePhone.Agent.Core.Calibration;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Tests;

public class CalibrationTests
{
    [Fact]
    public void LetterboxMapper_maps_center_click_to_content_midpoint()
    {
        // View 1000x500, content aspect 1.0 → letterbox (content 500x500 centered vertically? wait)
        // viewAspect = 2.0 > contentAspect 1.0 → pillarbox: contentH=500, contentW=500, offsetX=250
        var (nx, ny) = LetterboxMapper.NormalizedFromClick(
            clickX: 500,
            clickY: 250,
            viewW: 1000,
            viewH: 500,
            contentAspect: 1.0);

        nx.Should().BeApproximately(0.5, 0.001);
        ny.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void LetterboxMapper_clamps_clicks_in_bars_to_edges()
    {
        // Pillarbox: content is centered horizontally; left bar ends at offsetX=250
        var (nx, ny) = LetterboxMapper.NormalizedFromClick(
            clickX: 10,
            clickY: 250,
            viewW: 1000,
            viewH: 500,
            contentAspect: 1.0);

        nx.Should().Be(0.0);
        ny.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void HomingPlan_builds_upper_left_sweeps()
    {
        var steps = HomingPlan.BuildHomingSteps(maxStep: 50, sweeps: 40);

        steps.Should().HaveCount(40);
        steps.Should().OnlyContain(s => s.dx == -50 && s.dy == -50);
    }

    [Fact]
    public void Invalidate_on_orientation_change_concept()
    {
        var model = new PointerCalibrationModel
        {
            Orientation = ScreenOrientation.Portrait,
            ScaleX = 1.1,
            ScaleY = 0.95,
        };

        model.BeginCalibrate();
        model.MarkReady();
        model.State.Should().Be(CalibrationState.READY);
        var versionReady = model.Version;

        // Orientation flip invalidates calibration (concept exercised by Invalidate).
        var newOrientation = OrientationHelper.FromSize(width: 2532, height: 1170);
        newOrientation.Should().Be(ScreenOrientation.Landscape);
        model.Orientation = newOrientation;
        model.Invalidate();

        model.State.Should().Be(CalibrationState.INVALID);
        model.Version.Should().BeGreaterThan(versionReady);
    }

    [Fact]
    public void NormalizedToEstimatedPixel_applies_scales()
    {
        var model = new PointerCalibrationModel { ScaleX = 1.0, ScaleY = 1.0 };
        model.BeginCalibrate();
        model.MarkReady();

        var (x, y) = model.NormalizedToEstimatedPixel(0.5, 0.25, screenW: 200, screenH: 400);
        x.Should().Be(100);
        y.Should().Be(100);
    }

    [Fact]
    public void TapNormalized_rejects_out_of_range()
    {
        var act = () => new TapNormalized(1.5, 0.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
