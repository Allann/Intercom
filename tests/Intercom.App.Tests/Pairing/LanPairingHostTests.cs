using System.Net;
using System.Net.Sockets;
using Intercom.Identity;
using Intercom.Pairing;
using Intercom.Chat;
using Intercom.AttentionCards;
using Xunit;

namespace Intercom.App.Tests.Pairing;

public sealed class LanPairingHostTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "IntercomLanPairingTests_" + Guid.NewGuid());

    [Fact]
    public async Task TwoRealTlsHosts_ShowSameCode_AndBothPersistApproval()
    {
        var aStore = Store("a");
        var bStore = Store("b");
        var port = FreePort();

        await using var a = new LanPairingHost(aStore, port, pin =>
            pin == bStore.Identity.SpkiSha256 ? bStore.Identity.PeerId : null);
        await using var b = new LanPairingHost(bStore, port + 1, pin =>
            pin == aStore.Identity.SpkiSha256 ? aStore.Identity.PeerId : null);

        var aCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.PairingCodeReady += (_, code) => aCode.TrySetResult(code);
        b.PairingCodeReady += (_, code) => bCode.TrySetResult(code);
        a.Start();
        b.Start();

        await a.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, port + 1),
            bStore.Identity.PeerId,
            bStore.Identity.SpkiSha256,
            CancellationToken.None);

        Assert.Equal(await aCode.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            await bCode.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await a.ConfirmAsync(bStore.Identity.PeerId, "PC B", CancellationToken.None);
        await b.ConfirmAsync(aStore.Identity.PeerId, "PC A", CancellationToken.None);

        await WaitUntilAsync(() => aStore.ApprovedPeers.Count == 1 && bStore.ApprovedPeers.Count == 1);
        Assert.Equal(bStore.Identity.SpkiSha256, aStore.ApprovedPeers[0].SpkiSha256);
        Assert.Equal(aStore.Identity.SpkiSha256, bStore.ApprovedPeers[0].SpkiSha256);
        Assert.Equal("PC B", aStore.ApprovedPeers[0].FriendlyName);
        Assert.Equal("PC A", bStore.ApprovedPeers[0].FriendlyName);
    }

    [Fact]
    public async Task ApprovedPeers_ReconnectAfterRestart_AndExchangeRealChat()
    {
        var aInitial = Store("restart-a");
        var bInitial = Store("restart-b");
        aInitial.Approve(Peer(bInitial, "PC B"));
        bInitial.Approve(Peer(aInitial, "PC A"));
        var aStore = Reload("restart-a");
        var bStore = Reload("restart-b");
        var port = FreePort();

        await using var a = new LanPairingHost(aStore, port, _ => null);
        await using var b = new LanPairingHost(bStore, port + 1, _ => null);
        a.Start();
        b.Start();

        await a.ConnectApprovedAsync(
            new IPEndPoint(IPAddress.Loopback, port + 1),
            aStore.ApprovedPeers[0],
            CancellationToken.None);
        await WaitUntilAsync(() => a.ConnectedPeerIds.Contains(bStore.Identity.PeerId)
            && b.ConnectedPeerIds.Contains(aStore.Identity.PeerId));

        var aChat = new ChatService(a.CreateChatTransport(bStore.Identity.PeerId));
        var bChat = new ChatService(b.CreateChatTransport(aStore.Identity.PeerId));
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bChat.MessageReceived += message => received.TrySetResult(message);

        var sent = await aChat.SendAsync("hello from A", CancellationToken.None);
        Assert.Equal("hello from A", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        await WaitUntilAsync(() => aChat.Conversation.Messages
            .Single(message => message.MessageId == sent.MessageId).DeliveryState == ChatDeliveryState.Delivered);

        var aCards = new AttentionCardService(a.CreateAttentionCardTransport(bStore.Identity.PeerId));
        var bCards = new AttentionCardService(b.CreateAttentionCardTransport(aStore.Identity.PeerId));
        var cardReceived = new TaskCompletionSource<AttentionCard>(TaskCreationOptions.RunContinuationsAsynchronously);
        bCards.CardReceived += card => cardReceived.TrySetResult(card);
        var sentCard = await aCards.SendAsync("Dinner's ready", "🍽️", CancellationToken.None);
        var receivedCard = await cardReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Dinner's ready", receivedCard.Purpose);
        await bCards.AcknowledgeAsync(receivedCard.MessageId, CancellationToken.None);
        await WaitUntilAsync(() => aCards.Conversation.Cards
            .Single(card => card.MessageId == sentCard.MessageId).AckState == AttentionCardAckState.Acknowledged);
    }

    IdentityStore Store(string name)
    {
        var store = new IdentityStore(Path.Combine(_root, name));
        store.LoadOrCreate();
        return store;
    }

    IdentityStore Reload(string name)
    {
        var store = new IdentityStore(Path.Combine(_root, name));
        store.LoadOrCreate();
        return store;
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    static ApprovedPeer Peer(IdentityStore remote, string name) => new()
    {
        PeerId = remote.Identity.PeerId,
        FriendlyName = name,
        SpkiSha256 = remote.Identity.SpkiSha256,
        Certificate = remote.Identity.Certificate.RawData,
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
