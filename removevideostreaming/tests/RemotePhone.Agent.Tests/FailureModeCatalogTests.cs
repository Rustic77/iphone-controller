using FluentAssertions;
using RemotePhone.Agent.Core.Reliability;

namespace RemotePhone.Agent.Tests;

public class FailureModeCatalogTests
{
    [Fact]
    public void All_is_non_empty()
    {
        FailureModeCatalog.All().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("AirPlay")]
    [InlineData("WebRTC")]
    [InlineData("Capture")]
    public void Catalog_covers_keyword(string keyword)
    {
        var allText = string.Join(
            '\n',
            FailureModeCatalog.All().Select(m =>
                $"{m.Trigger}\n{m.ExpectedBehavior}\n{m.AutomaticProtection}\n{m.RecoveryProcedure}\n{m.PassCondition}"));

        allText.Should().Contain(keyword);
    }

    [Fact]
    public void Each_entry_has_required_fields()
    {
        foreach (var mode in FailureModeCatalog.All())
        {
            mode.Trigger.Should().NotBeNullOrWhiteSpace();
            mode.ExpectedBehavior.Should().NotBeNullOrWhiteSpace();
            mode.AutomaticProtection.Should().NotBeNullOrWhiteSpace();
            mode.RecoveryProcedure.Should().NotBeNullOrWhiteSpace();
            mode.PassCondition.Should().NotBeNullOrWhiteSpace();
        }
    }
}
