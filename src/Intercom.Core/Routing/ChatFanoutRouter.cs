using Intercom.Chat;
using Intercom.Presence;

namespace Intercom.Routing;

/// <summary>One approved device's live chat endpoint within a contact's
/// fan-out set.</summary>
public sealed record ChatDeviceEndpoint
{
    public required Guid DeviceId { get; init; }
    public required ChatService Service { get; init; }
}

/// <summary>
/// Real routing for one contact's text chat (issue #30): ranks the contact's
/// live devices (<see cref="DeviceRoutingPolicy"/>, honoring the manual
/// override), sends first to the top-ranked device, and fans out to every
/// other live eligible device if no mechanical
/// <see cref="ChatDeliveryState.Delivered"/> receipt arrives within
/// <see cref="InitialAckTimeout"/> or the devices were tied
/// (docs/research/active-device-presence.md "Text and attention cards";
/// docs/adr/0004-multi-device-contact-routing.md's 3-second initial-ack
/// timeout / tie definition).
///
/// "Follow me" (research doc, ADR-0004): re-ranking only ever happens at the
/// START of a new <see cref="SendAsync"/> call — an already-sent message is
/// never re-routed or migrated once <see cref="SendAsync"/> has returned, so
/// an established conversation cannot be silently transferred mid-flight by
/// construction, with no separate enforcement needed.
///
/// Deliberately simpler than <c>AttentionCardFanoutRouter</c>: chat has no
/// human acknowledgement (ADR-0001 — "no read receipt"), so there is no
/// "first ack wins"/<c>resolved()</c> withdrawal concept for chat at all — a
/// fanned-out chat message landing on more than one of a person's devices is
/// simply visible on each; nothing to dedup or take back. Each device gets
/// its own independently-minted MessageId (via that device's own
/// <see cref="ChatService.SendAsync"/>) rather than one shared interaction
/// ID — with no ack/dedup consumer for that ID on the chat side, sharing one
/// would add complexity with no behavioral payoff.
///
/// No live multi-peer connection roster exists in this app shell yet (see
/// App.xaml.cs's <c>StartPresence</c> doc comment, and #24/#25's Loopback
/// transports) — this class is real, tested routing logic, exercised in
/// tests against hand-written fakes/loopback transports, not wired into any
/// live UI today.
/// </summary>
public sealed class ChatFanoutRouter
{
    /// <summary>docs/adr/0004-multi-device-contact-routing.md: "3 seconds to
    /// receive the mechanical Delivered receipt... before a text/attention-
    /// card send widens from the single ranked device to every live eligible
    /// device."</summary>
    public static readonly TimeSpan InitialAckTimeout = TimeSpan.FromSeconds(3);

    readonly IReadOnlyDictionary<Guid, ChatDeviceEndpoint> _endpoints;
    readonly Func<Guid?> _overrideDeviceId;
    readonly Action? _onOverrideLapsed;
    readonly Func<DateTimeOffset> _clock;
    readonly TimeSpan _initialAckTimeout;

    /// <param name="endpoints">One entry per approved device currently in the
    /// contact's grouping.</param>
    /// <param name="overrideDeviceId">Reads the contact's current manual
    /// override (see <see cref="ManualOverrideStore.Get"/>) at the start of
    /// each send.</param>
    /// <param name="onOverrideLapsed">Called when
    /// <see cref="DeviceRoutingPolicy"/> reports the override auto-lapsed —
    /// wire this to <see cref="ManualOverrideStore.Clear"/>.</param>
    /// <param name="initialAckTimeout">Overridable only for tests; production
    /// callers should leave this at <see cref="InitialAckTimeout"/>.</param>
    public ChatFanoutRouter(
        IReadOnlyDictionary<Guid, ChatDeviceEndpoint> endpoints,
        Func<Guid?> overrideDeviceId,
        Action? onOverrideLapsed = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? initialAckTimeout = null)
    {
        _endpoints = endpoints;
        _overrideDeviceId = overrideDeviceId;
        _onOverrideLapsed = onOverrideLapsed;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _initialAckTimeout = initialAckTimeout ?? InitialAckTimeout;
    }

    /// <param name="liveContactDevices">This contact's currently-live tracked
    /// devices (see <see cref="PresenceReceiver.LiveDevices"/>), evaluated
    /// fresh by the caller right before calling this — ranking always uses
    /// the current snapshot, never a cached one.</param>
    public async Task SendAsync(IReadOnlyList<TrackedDevicePresence> liveContactDevices, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(liveContactDevices);

        var decision = DeviceRoutingPolicy.ResolveTarget(_overrideDeviceId(), liveContactDevices, InteractionKind.Text, _clock());
        if (decision.OverrideLapsed) _onOverrideLapsed?.Invoke();

        if (decision.RankedDevices.Count == 0)
        {
            throw new InvalidOperationException("No live eligible device for this contact's chat message.");
        }

        var top = decision.RankedDevices[0];
        if (!_endpoints.TryGetValue(top.DeviceId, out var topEndpoint))
        {
            throw new InvalidOperationException($"No chat endpoint configured for device {top.DeviceId}.");
        }

        ChatMessage? sent = null;
        try
        {
            sent = await topEndpoint.Service.SendAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // topEndpoint.Service.SendAsync already marked its own message
            // Undelivered — still attempt fan-out to the rest below.
        }

        var others = decision.RankedDevices.Skip(1).ToList();
        bool shouldFanout;
        if (decision.IsTie)
        {
            shouldFanout = true;
        }
        else if (sent is null)
        {
            shouldFanout = others.Count > 0; // top device's send itself failed outright
        }
        else if (others.Count > 0)
        {
            var delivered = await WaitForDeliveryAsync(topEndpoint, sent.MessageId, cancellationToken).ConfigureAwait(false);
            shouldFanout = !delivered;
        }
        else
        {
            shouldFanout = false;
        }

        if (!shouldFanout) return;

        foreach (var device in others)
        {
            if (!_endpoints.TryGetValue(device.DeviceId, out var endpoint)) continue;
            try
            {
                await endpoint.Service.SendAsync(text, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort fan-out, mirrors PeerControlChannel.TrySendAsync's
                // "the next observation of a dead connection handles it"
                // reasoning — one sibling failing to send must not abort
                // fanning out to the rest.
            }
        }
    }

    /// <summary>Waits on the actual Delivered/Undelivered transition for
    /// <paramref name="messageId"/> — never polling a proxy value — racing it
    /// against <see cref="_initialAckTimeout"/>.</summary>
    async Task<bool> WaitForDeliveryAsync(ChatDeviceEndpoint endpoint, Guid messageId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(ChatMessage message)
        {
            if (message.MessageId == messageId && message.DeliveryState != ChatDeliveryState.Pending)
            {
                tcs.TrySetResult(message.DeliveryState == ChatDeliveryState.Delivered);
            }
        }

        endpoint.Service.Conversation.MessageUpdated += OnUpdated;
        try
        {
            // The transport may confirm delivery synchronously, inside the
            // SendAsync call this method's caller already awaited — before
            // this subscription even exists. Check the CURRENT state first
            // so that already-resolved case is not missed and mistaken for a
            // timeout.
            var current = endpoint.Service.Conversation.Messages.FirstOrDefault(m => m.MessageId == messageId);
            if (current is not null && current.DeliveryState != ChatDeliveryState.Pending)
            {
                return current.DeliveryState == ChatDeliveryState.Delivered;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_initialAckTimeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false; // the 3-second window elapsed with no Delivered
            }
        }
        finally
        {
            endpoint.Service.Conversation.MessageUpdated -= OnUpdated;
        }
    }
}
