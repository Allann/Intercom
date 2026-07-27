using Intercom.AttentionCards;
using Intercom.ControlChannel;
using Intercom.Presence;

namespace Intercom.Routing;

/// <summary>One approved device's live attention-card endpoint within a
/// contact's fan-out set: the per-device <see cref="AttentionCardService"/>
/// (owns that device's own <see cref="AttentionCardConversation"/> and
/// inbound wiring) plus direct access to its
/// <see cref="IAttentionCardTransport"/> — needed only because fan-out
/// requires sending the SAME MessageId ("interaction ID") to every device
/// (docs/research/active-device-presence.md: "send the same interaction ID
/// to every live eligible device" / "idempotent by interaction ID"), which
/// <see cref="AttentionCardService.SendAsync"/> cannot do on its own since it
/// always mints a fresh ID.</summary>
public sealed record AttentionCardDeviceEndpoint
{
    public required Guid DeviceId { get; init; }
    public required AttentionCardService Service { get; init; }
    public required IAttentionCardTransport Transport { get; init; }
}

/// <summary>
/// Real routing for one contact's attention cards (issue #30;
/// docs/adr/0004-multi-device-contact-routing.md): ranks the contact's live
/// devices (via <see cref="DeviceRoutingPolicy"/>, honoring the manual
/// override), sends first to the top-ranked device, and fans out the SAME
/// card (same MessageId = "interaction ID") to every other live eligible
/// device if no <see cref="AttentionCardDeliveryState.Delivered"/> receipt
/// arrives within <see cref="InitialAckTimeout"/> or the devices were tied.
///
/// The first device whose peer acknowledges wins (ADR-0001's Acknowledged is
/// exclusive to attention cards — chat has no equivalent, see
/// <see cref="ChatFanoutRouter"/>'s doc for why that router has no
/// resolved()/withdrawal concept at all): this router then broadcasts a
/// <see cref="ControlMessageType.Resolved"/> frame (issue #30's new wire
/// message) to the OTHER devices that received the same interaction, so they
/// withdraw the duplicate — <see cref="CardWithdrawn"/> is how a caller (the
/// shelf UI) observes that on the receiving end.
///
/// Independent of any of that, every RECEIVED card carries its own local
/// 5-minute expiry from the moment it arrived (ADR-0004's "Duplicate-
/// notification expiry": "if [the sender] goes offline before relaying, the
/// card just drops locally on its own after 5 minutes"), checked explicitly
/// via <see cref="PruneExpiredReceivedCards"/> against a caller-supplied
/// clock reading rather than a real background timer, so it stays
/// deterministically testable — a periodic app-level tick (mirroring
/// <see cref="PeerControlChannel.Tick"/>'s pattern) is expected to drive that
/// in production once this is wired into a live UI.
///
/// "Follow me": exactly like <see cref="ChatFanoutRouter"/>, re-ranking only
/// happens at the START of a new <see cref="SendAsync"/> call.
///
/// No live multi-peer connection roster exists in this app shell yet (same
/// scope note App.xaml.cs's <c>StartPresence</c> and #24/#25's Loopback
/// transports document) — this class is real, tested routing logic, wired
/// today only against hand-written fakes/loopback transports in tests, not
/// against any live UI.
/// </summary>
public sealed class AttentionCardFanoutRouter
{
    /// <summary>docs/adr/0004-multi-device-contact-routing.md's initial-ack
    /// timeout.</summary>
    public static readonly TimeSpan InitialAckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>docs/adr/0004-multi-device-contact-routing.md's
    /// duplicate-notification expiry.</summary>
    public static readonly TimeSpan LocalReceivedCardExpiry = TimeSpan.FromMinutes(5);

    readonly IReadOnlyDictionary<Guid, AttentionCardDeviceEndpoint> _endpoints;
    readonly Func<Guid?> _overrideDeviceId;
    readonly Action? _onOverrideLapsed;
    readonly Func<DateTimeOffset> _clock;
    readonly TimeSpan _initialAckTimeout;

    readonly object _gate = new();

    // interactionId -> the device IDs it was actually sent to (top + any
    // fan-out). Consumed (and removed) the instant the interaction is
    // resolved, to compute who still needs a Resolved broadcast.
    readonly Dictionary<Guid, List<Guid>> _fanoutTargetsByInteraction = [];

    // Dedup for "first ack wins": an interaction ID is added here the first
    // time ANY device's Acknowledged receipt is observed; a second (racing)
    // Acknowledged for the same interaction is then a no-op.
    readonly HashSet<Guid> _resolvedInteractionIds = [];

    // Local receive time per RECEIVED (not sent) interaction still eligible
    // for its own independent 5-minute expiry. Removed once acknowledged,
    // withdrawn, or expired.
    readonly Dictionary<Guid, DateTimeOffset> _receivedCardArrivalByInteraction = [];

    // interaction IDs this device has been told (via inbound Resolved) to
    // withdraw — guards against acting on a duplicate Resolved and against
    // acknowledging an already-withdrawn card.
    readonly HashSet<Guid> _withdrawnInteractionIds = [];

    /// <summary>Raised on the RECEIVING side when an inbound
    /// <see cref="ControlMessageType.Resolved"/> frame withdraws a
    /// not-yet-acknowledged received card — the shelf UI should remove it.</summary>
    public event Action<Guid>? CardWithdrawn;

    /// <summary>Raised by <see cref="PruneExpiredReceivedCards"/> for a
    /// received card whose local 5-minute window lapsed without ever being
    /// acknowledged or withdrawn.</summary>
    public event Action<Guid>? CardExpiredLocally;

    public AttentionCardFanoutRouter(
        IReadOnlyDictionary<Guid, AttentionCardDeviceEndpoint> endpoints,
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

        foreach (var endpoint in _endpoints.Values)
        {
            var deviceId = endpoint.DeviceId;
            endpoint.Transport.Acknowledged += interactionId => OnDeviceAcknowledged(deviceId, interactionId);
            endpoint.Transport.Resolved += OnResolvedReceived;
            endpoint.Service.CardReceived += OnCardReceived;
        }
    }

    /// <param name="liveContactDevices">This contact's currently-live tracked
    /// devices, evaluated fresh by the caller right before calling this.</param>
    public async Task<AttentionCard> SendAsync(
        IReadOnlyList<TrackedDevicePresence> liveContactDevices,
        string purpose,
        string icon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(liveContactDevices);

        var now = _clock();
        var decision = DeviceRoutingPolicy.ResolveTarget(_overrideDeviceId(), liveContactDevices, InteractionKind.AttentionChime, now);
        if (decision.OverrideLapsed) _onOverrideLapsed?.Invoke();

        if (decision.RankedDevices.Count == 0)
        {
            throw new InvalidOperationException("No live eligible device for this contact's attention card.");
        }

        var interactionId = Guid.NewGuid();
        var frame = AttentionCardFrameCodec.ToFrame(purpose, icon, interactionId);

        var top = decision.RankedDevices[0];
        if (!_endpoints.TryGetValue(top.DeviceId, out var topEndpoint))
        {
            throw new InvalidOperationException($"No attention-card endpoint configured for device {top.DeviceId}.");
        }

        // Registered BEFORE anything is sent — see class remarks on
        // RegisterSentToAsync: this closes the race where a device's peer
        // acknowledges (or even just receives an already-resolved
        // duplicate) faster than this method gets around to recording that
        // it was ever sent there at all.
        lock (_gate) { _fanoutTargetsByInteraction[interactionId] = []; }

        var card = SendToDevice(topEndpoint, frame, interactionId, purpose, icon);
        await RegisterSentToAsync(interactionId, top.DeviceId, topEndpoint).ConfigureAwait(false);
        var delivered = await AwaitSendAsync(topEndpoint, frame, cancellationToken).ConfigureAwait(false);

        var others = decision.RankedDevices.Skip(1).ToList();
        bool shouldFanout;
        if (decision.IsTie)
        {
            shouldFanout = true;
        }
        else if (!delivered)
        {
            shouldFanout = others.Count > 0; // the top send itself failed outright
        }
        else if (others.Count > 0)
        {
            var deliveredInTime = await WaitForDeliveryAsync(topEndpoint, interactionId, cancellationToken).ConfigureAwait(false);
            shouldFanout = !deliveredInTime;
        }
        else
        {
            shouldFanout = false;
        }

        if (shouldFanout)
        {
            foreach (var device in others)
            {
                if (!_endpoints.TryGetValue(device.DeviceId, out var endpoint)) continue;
                SendToDevice(endpoint, frame, interactionId, purpose, icon);
                await RegisterSentToAsync(interactionId, device.DeviceId, endpoint).ConfigureAwait(false);
                await AwaitSendAsync(endpoint, frame, cancellationToken).ConfigureAwait(false);
            }
        }

        return card;
    }

    AttentionCard SendToDevice(AttentionCardDeviceEndpoint endpoint, ControlFrame frame, Guid interactionId, string purpose, string icon) =>
        endpoint.Service.Conversation.AddOutbound(interactionId, purpose, icon);

    /// <summary>Records that <paramref name="deviceId"/> is (about to be) a
    /// recipient of <paramref name="interactionId"/> — called BEFORE the
    /// actual transport send, so <see cref="OnDeviceAcknowledged"/> can never
    /// observe an ack for a device this bookkeeping doesn't know about yet.
    /// If the interaction was already resolved by the time this device gets
    /// its turn (a faster sibling's ack raced ahead of this still-in-progress
    /// fan-out loop, most plausible during an ADR-0004 tie burst where every
    /// device is sent to nearly at once), this device is about to receive a
    /// duplicate of an already-won interaction — send it its own Resolved
    /// withdrawal immediately rather than leaving it to linger until its
    /// local 5-minute expiry.</summary>
    async Task RegisterSentToAsync(Guid interactionId, Guid deviceId, AttentionCardDeviceEndpoint endpoint)
    {
        bool alreadyResolved;
        lock (_gate)
        {
            alreadyResolved = _resolvedInteractionIds.Contains(interactionId);
            if (!alreadyResolved && _fanoutTargetsByInteraction.TryGetValue(interactionId, out var targets))
            {
                targets.Add(deviceId);
            }
        }

        if (!alreadyResolved) return;

        var resolvedFrame = new ControlFrame
        {
            Type = ControlMessageType.Resolved,
            MessageId = Guid.NewGuid(),
            CorrelationId = interactionId,
            Payload = [],
        };
        try
        {
            await endpoint.Transport.SendAsync(resolvedFrame, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort, same reasoning as BroadcastResolvedAsync.
        }
    }

    async Task<bool> AwaitSendAsync(AttentionCardDeviceEndpoint endpoint, ControlFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            await endpoint.Transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            endpoint.Service.Conversation.MarkUndelivered(frame.MessageId);
            return false;
        }
    }

    /// <summary>Waits on the actual Delivered/Undelivered transition for
    /// <paramref name="interactionId"/> — never polling a proxy value —
    /// racing it against <see cref="_initialAckTimeout"/>.</summary>
    async Task<bool> WaitForDeliveryAsync(AttentionCardDeviceEndpoint endpoint, Guid interactionId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(AttentionCard card)
        {
            if (card.MessageId == interactionId && card.DeliveryState != AttentionCardDeliveryState.Pending)
            {
                tcs.TrySetResult(card.DeliveryState == AttentionCardDeliveryState.Delivered);
            }
        }

        endpoint.Service.Conversation.CardUpdated += OnUpdated;
        try
        {
            // See ChatFanoutRouter.WaitForDeliveryAsync's identical remark:
            // the transport may confirm delivery synchronously inside the
            // send this method's caller already awaited, before this
            // subscription existed — check current state first.
            var current = endpoint.Service.Conversation.Cards.FirstOrDefault(c => c.MessageId == interactionId);
            if (current is not null && current.DeliveryState != AttentionCardDeliveryState.Pending)
            {
                return current.DeliveryState == AttentionCardDeliveryState.Delivered;
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
            endpoint.Service.Conversation.CardUpdated -= OnUpdated;
        }
    }

    void OnDeviceAcknowledged(Guid ackingDeviceId, Guid interactionId)
    {
        List<Guid>? targets = null;
        lock (_gate)
        {
            // First ack wins: a second (racing) Acknowledged for an
            // already-resolved interaction is silently ignored.
            if (!_resolvedInteractionIds.Add(interactionId)) return;
            if (_fanoutTargetsByInteraction.Remove(interactionId, out var sentTo))
            {
                targets = sentTo.Where(id => id != ackingDeviceId).ToList();
            }
        }

        if (targets is { Count: > 0 })
        {
            _ = BroadcastResolvedAsync(targets, interactionId);
        }
    }

    async Task BroadcastResolvedAsync(List<Guid> targets, Guid interactionId)
    {
        foreach (var deviceId in targets)
        {
            if (!_endpoints.TryGetValue(deviceId, out var endpoint)) continue;
            var frame = new ControlFrame
            {
                Type = ControlMessageType.Resolved,
                MessageId = Guid.NewGuid(),
                CorrelationId = interactionId,
                Payload = [],
            };
            try
            {
                await endpoint.Transport.SendAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort, mirrors PeerControlChannel.TrySendAsync — the
                // sender is only a temporary transaction coordinator (research
                // doc), not a guaranteed-delivery server; a sibling that
                // misses the broadcast still self-expires after 5 minutes.
            }
        }
    }

    void OnCardReceived(AttentionCard card)
    {
        if (card.Direction != AttentionCardDirection.Received) return;
        lock (_gate) { _receivedCardArrivalByInteraction[card.MessageId] = _clock(); }
    }

    void OnResolvedReceived(Guid interactionId)
    {
        lock (_gate)
        {
            if (!_withdrawnInteractionIds.Add(interactionId)) return;
            _receivedCardArrivalByInteraction.Remove(interactionId);
        }
        CardWithdrawn?.Invoke(interactionId);
    }

    /// <summary>Acknowledges a received card on <paramref name="deviceId"/>'s
    /// endpoint, unless it has already been withdrawn by an inbound
    /// <see cref="ControlMessageType.Resolved"/> — a device must not send an
    /// Acknowledged for an interaction it was just told someone else already
    /// won. Returns false (no-op) if withdrawn or the device is unknown.</summary>
    public async Task<bool> TryAcknowledgeAsync(Guid deviceId, Guid interactionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_withdrawnInteractionIds.Contains(interactionId)) return false;
        }
        if (!_endpoints.TryGetValue(deviceId, out var endpoint)) return false;

        await endpoint.Service.AcknowledgeAsync(interactionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Call periodically (see the class remarks) with the current
    /// time to drop any received card past its own 5-minute local expiry,
    /// raising <see cref="CardExpiredLocally"/> for each one.</summary>
    public void PruneExpiredReceivedCards(DateTimeOffset now)
    {
        List<Guid> expired;
        lock (_gate)
        {
            expired = _receivedCardArrivalByInteraction
                .Where(kv => now - kv.Value >= LocalReceivedCardExpiry)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in expired) _receivedCardArrivalByInteraction.Remove(id);
        }
        foreach (var id in expired) CardExpiredLocally?.Invoke(id);
    }
}
