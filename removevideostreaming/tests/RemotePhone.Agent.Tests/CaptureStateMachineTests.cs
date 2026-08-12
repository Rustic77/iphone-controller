using FluentAssertions;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Tests;

public class CaptureStateMachineTests
{
    [Fact]
    public void Starts_in_Idle()
    {
        var sm = new CaptureStateMachine();
        sm.Current.Should().Be(CaptureState.Idle);
    }

    [Theory]
    [InlineData(CaptureState.Idle, CaptureState.Selecting)]
    [InlineData(CaptureState.Selecting, CaptureState.Capturing)]
    [InlineData(CaptureState.Selecting, CaptureState.Idle)]
    [InlineData(CaptureState.Capturing, CaptureState.SourceLost)]
    [InlineData(CaptureState.Capturing, CaptureState.Stopped)]
    [InlineData(CaptureState.Capturing, CaptureState.Error)]
    [InlineData(CaptureState.SourceLost, CaptureState.Capturing)]
    [InlineData(CaptureState.SourceLost, CaptureState.Stopped)]
    [InlineData(CaptureState.SourceLost, CaptureState.Idle)]
    [InlineData(CaptureState.Stopped, CaptureState.Idle)]
    [InlineData(CaptureState.Stopped, CaptureState.Selecting)]
    [InlineData(CaptureState.Error, CaptureState.Idle)]
    [InlineData(CaptureState.Error, CaptureState.Selecting)]
    public void TryTransition_allows_legal_edges(CaptureState from, CaptureState to)
    {
        var sm = new CaptureStateMachine();
        ForceState(sm, from);

        sm.TryTransition(to).Should().BeTrue();
        sm.Current.Should().Be(to);
    }

    [Theory]
    [InlineData(CaptureState.Idle, CaptureState.Capturing)]
    [InlineData(CaptureState.Idle, CaptureState.Stopped)]
    [InlineData(CaptureState.Capturing, CaptureState.Selecting)]
    [InlineData(CaptureState.Capturing, CaptureState.Idle)]
    [InlineData(CaptureState.Stopped, CaptureState.Capturing)]
    [InlineData(CaptureState.Error, CaptureState.Capturing)]
    public void TryTransition_rejects_illegal_edges(CaptureState from, CaptureState to)
    {
        var sm = new CaptureStateMachine();
        ForceState(sm, from);
        var before = sm.Current;

        sm.TryTransition(to).Should().BeFalse();
        sm.Current.Should().Be(before);
    }

    [Fact]
    public void TryTransition_raises_StateChanged_only_on_success()
    {
        var sm = new CaptureStateMachine();
        var raised = new List<CaptureState>();
        sm.StateChanged += (_, state) => raised.Add(state);

        sm.TryTransition(CaptureState.Capturing).Should().BeFalse();
        raised.Should().BeEmpty();

        sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
        raised.Should().Equal(CaptureState.Selecting);
    }

    [Fact]
    public void Happy_path_Idle_Selecting_Capturing_Stopped_Idle()
    {
        var sm = new CaptureStateMachine();
        sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
        sm.TryTransition(CaptureState.Capturing).Should().BeTrue();
        sm.TryTransition(CaptureState.Stopped).Should().BeTrue();
        sm.TryTransition(CaptureState.Idle).Should().BeTrue();
        sm.Current.Should().Be(CaptureState.Idle);
    }

    private static void ForceState(CaptureStateMachine sm, CaptureState target)
    {
        if (sm.Current == target)
        {
            return;
        }

        // Walk a known legal path to the requested state.
        switch (target)
        {
            case CaptureState.Idle:
                return;
            case CaptureState.Selecting:
                sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
                break;
            case CaptureState.Capturing:
                sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
                sm.TryTransition(CaptureState.Capturing).Should().BeTrue();
                break;
            case CaptureState.SourceLost:
                sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
                sm.TryTransition(CaptureState.Capturing).Should().BeTrue();
                sm.TryTransition(CaptureState.SourceLost).Should().BeTrue();
                break;
            case CaptureState.Stopped:
                sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
                sm.TryTransition(CaptureState.Capturing).Should().BeTrue();
                sm.TryTransition(CaptureState.Stopped).Should().BeTrue();
                break;
            case CaptureState.Error:
                sm.TryTransition(CaptureState.Selecting).Should().BeTrue();
                sm.TryTransition(CaptureState.Capturing).Should().BeTrue();
                sm.TryTransition(CaptureState.Error).Should().BeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }

        sm.Current.Should().Be(target);
    }
}
