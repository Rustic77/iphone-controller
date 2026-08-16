using FluentAssertions;
using RemotePhone.Agent.Core.Capture;

namespace RemotePhone.Agent.Tests;

public class BgraScalerTests
{
    [Fact]
    public void Fit_passthrough_when_already_small_and_even()
    {
        var src = new byte[8 * 10 * 4];
        var result = BgraScaler.Fit(src, 8, 10, maxLongEdge: 1280, out var w, out var h);
        result.Should().BeSameAs(src);
        w.Should().Be(8);
        h.Should().Be(10);
    }

    [Fact]
    public void Fit_downscales_long_edge_and_keeps_even_dims()
    {
        const int width = 1170;
        const int height = 2532;
        var src = new byte[width * height * 4];
        src[0] = 10;
        src[1] = 20;
        src[2] = 30;
        src[3] = 255;

        var result = BgraScaler.Fit(src, width, height, maxLongEdge: 1280, out var w, out var h);
        Math.Max(w, h).Should().BeLessThanOrEqualTo(1280);
        (w % 2).Should().Be(0);
        (h % 2).Should().Be(0);
        result.Length.Should().Be(w * h * 4);
        result.Should().NotBeSameAs(src);
    }
}
