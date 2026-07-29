using System.Net;
using Intercom.Discovery;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public class LanDiscoveryProbeTests
{
    [Fact]
    public async Task AlreadyRunningPeer_AnswersNewPeersStartupQuery()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        using var first = new LanDiscoveryProbe(firstId, Pin(1), 47811);
        using var second = new LanDiscoveryProbe(secondId, Pin(2), 47811);
        var seen = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.PeerAnswered += (peerId, spki, endpoint) =>
        {
            if (peerId == firstId && spki == Pin(1)) seen.TrySetResult(endpoint);
        };

        first.Start();
        await Task.Delay(50);
        second.Start();

        var endpoint = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(47811, endpoint.Port);
    }

    static SpkiPin Pin(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
}
