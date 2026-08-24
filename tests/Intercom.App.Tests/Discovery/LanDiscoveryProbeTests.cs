using System.Net;
using Intercom.Discovery;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public class LanDiscoveryProbeTests
{
    [Fact]
    public void AlreadyRunningPeer_AnswersNewPeersStartupQuery()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        using var second = new LanDiscoveryProbe(secondId, Pin(2), 47811);
        IPEndPoint? seen = null;
        second.PeerAnswered += (peerId, spki, endpoint) =>
        {
            if (peerId == firstId && spki == Pin(1)) seen = endpoint;
        };

        var datagram = System.Text.Encoding.UTF8.GetBytes(
            $"INTERCOM-DISCOVERY/1|HERE|{firstId:N}|47811|{Pin(1)}");
        second.ProcessReceived(datagram, new IPEndPoint(IPAddress.Loopback, LanDiscoveryProbe.Port));

        Assert.NotNull(seen);
        Assert.Equal(47811, seen.Port);
    }

    [Fact]
    public void ExactStartupQuery_SendsThisPeersCompleteAdvertisement()
    {
        var peerId = Guid.NewGuid();
        var sent = new List<string>();
        using var probe = new LanDiscoveryProbe(peerId, Pin(2), 47811) { SendOverride = sent.Add };

        probe.ProcessReceived(
            System.Text.Encoding.UTF8.GetBytes("INTERCOM-DISCOVERY/1|QUERY"),
            Remote());

        Assert.Equal([$"INTERCOM-DISCOVERY/1|HERE|{peerId:N}|47811|{Pin(2)}"], sent);
    }

    [Fact]
    public void NearMatchStartupQuery_DoesNotSendAnAdvertisement()
    {
        var sent = new List<string>();
        using var probe = new LanDiscoveryProbe(Guid.NewGuid(), Pin(2), 47811) { SendOverride = sent.Add };

        probe.ProcessReceived(
            System.Text.Encoding.UTF8.GetBytes("INTERCOM-DISCOVERY/1|query"),
            Remote());

        Assert.Empty(sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|extra|fields|make|six")]
    [InlineData("WRONG|HERE|00000000000000000000000000000001|47811|00")]
    [InlineData("INTERCOM-DISCOVERY/1|WRONG|00000000000000000000000000000001|47811|00")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|not-a-guid|47811|00")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|00000000000000000000000000000001|0|00")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|00000000000000000000000000000001|-1|00")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|00000000000000000000000000000001|65536|00")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|00000000000000000000000000000001|not-a-port|00")]
    [InlineData("INTERCOM-DISCOVERY/1|HERE|00000000000000000000000000000001|47811|not-hex")]
    public void InvalidAdvertisement_IsIgnored(string text)
    {
        using var probe = new LanDiscoveryProbe(Guid.NewGuid(), Pin(2), 47811);
        var raised = false;
        probe.PeerAnswered += (_, _, _) => raised = true;

        probe.ProcessReceived(
            System.Text.Encoding.UTF8.GetBytes(text),
            new IPEndPoint(IPAddress.Loopback, LanDiscoveryProbe.Port));

        Assert.False(raised);
    }

    [Fact]
    public void OwnAdvertisement_IsIgnored()
    {
        var peerId = Guid.NewGuid();
        using var probe = new LanDiscoveryProbe(peerId, Pin(2), 47811);
        var raised = false;
        probe.PeerAnswered += (_, _, _) => raised = true;

        probe.ProcessReceived(
            System.Text.Encoding.UTF8.GetBytes(
                $"INTERCOM-DISCOVERY/1|HERE|{peerId:N}|47811|{Pin(2)}"),
            new IPEndPoint(IPAddress.Loopback, LanDiscoveryProbe.Port));

        Assert.False(raised);
    }

    [Fact]
    public void Advertisement_RequiresEveryEnvelopePartWithOtherwiseValidData()
    {
        var remotePeer = Guid.NewGuid();
        using var probe = new LanDiscoveryProbe(Guid.NewGuid(), Pin(2), 47811);
        var raised = 0;
        probe.PeerAnswered += (_, _, _) => raised++;
        var validTail = $"{remotePeer:N}|47811|{Pin(1)}";

        probe.ProcessReceived(System.Text.Encoding.UTF8.GetBytes($"WRONG|HERE|{validTail}"), Remote());
        probe.ProcessReceived(System.Text.Encoding.UTF8.GetBytes($"INTERCOM-DISCOVERY/1|WRONG|{validTail}"), Remote());
        probe.ProcessReceived(System.Text.Encoding.UTF8.GetBytes($"INTERCOM-DISCOVERY/1|HERE|{validTail}|EXTRA"), Remote());

        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("65536")]
    public void Advertisement_RequiresAValidPortWithOtherwiseValidData(string port)
    {
        var remotePeer = Guid.NewGuid();
        using var probe = new LanDiscoveryProbe(Guid.NewGuid(), Pin(2), 47811);
        var raised = 0;
        probe.PeerAnswered += (_, _, _) => raised++;

        probe.ProcessReceived(
            System.Text.Encoding.UTF8.GetBytes($"INTERCOM-DISCOVERY/1|HERE|{remotePeer:N}|{port}|{Pin(1)}"), Remote());

        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void Advertisement_AcceptsInclusivePortBoundaries(int port)
    {
        var remotePeer = Guid.NewGuid();
        using var probe = new LanDiscoveryProbe(Guid.NewGuid(), Pin(2), 47811);
        IPEndPoint? endpoint = null;
        probe.PeerAnswered += (_, _, received) => endpoint = received;

        probe.ProcessReceived(
            System.Text.Encoding.UTF8.GetBytes($"INTERCOM-DISCOVERY/1|HERE|{remotePeer:N}|{port}|{Pin(1)}"),
            new IPEndPoint(IPAddress.Loopback, LanDiscoveryProbe.Port));

        Assert.Equal(port, Assert.IsType<IPEndPoint>(endpoint).Port);
    }

    static SpkiPin Pin(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
    static IPEndPoint Remote() => new(IPAddress.Loopback, LanDiscoveryProbe.Port);
}
