using FluentAssertions;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Tests;

public class OrientationHelperTests
{
    [Theory]
    [InlineData(1170, 2532, ScreenOrientation.Portrait)]
    [InlineData(720, 1280, ScreenOrientation.Portrait)]
    [InlineData(1, 2, ScreenOrientation.Portrait)]
    public void FromSize_returns_portrait_when_height_greater(int width, int height, ScreenOrientation expected)
    {
        OrientationHelper.FromSize(width, height).Should().Be(expected);
    }

    [Theory]
    [InlineData(2532, 1170, ScreenOrientation.Landscape)]
    [InlineData(1280, 720, ScreenOrientation.Landscape)]
    [InlineData(1080, 1080, ScreenOrientation.Landscape)]
    [InlineData(2, 1, ScreenOrientation.Landscape)]
    public void FromSize_returns_landscape_when_width_gte_height(int width, int height, ScreenOrientation expected)
    {
        OrientationHelper.FromSize(width, height).Should().Be(expected);
    }
}
