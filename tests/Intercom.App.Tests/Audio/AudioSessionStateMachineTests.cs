using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioSessionStateMachineTests
{
    [Fact]
    public void LifecycleSupportsDegradeRecoveryAndIdempotentStop()
    {
        var state = new AudioSessionStateMachine();
        Assert.True(state.Start());
        Assert.True(state.Started());
        Assert.True(state.Degrade());
        Assert.True(state.Recover());
        Assert.True(state.BeginStop());
        Assert.False(state.BeginStop());
        Assert.True(state.Stopped());
        Assert.Equal(AudioSessionState.Stopped, state.State);
    }
}
