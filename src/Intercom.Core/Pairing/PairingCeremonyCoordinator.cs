using Intercom.ControlChannel;
using Intercom.Identity;

namespace Intercom.Pairing;

/// <summary>
/// Orchestrates one pairing ceremony against one peer's live
/// <see cref="ConnectionTrust.PairingOnly"/> connection: generates this
/// side's nonce, exchanges <see cref="PairingNonceMessage"/> frames, computes
/// the shared six-digit SAS once both nonces are known
/// (<see cref="PairingTranscript"/>), tracks confirmation state
/// (<see cref="PairingCeremony"/>), and — the instant both sides have
/// confirmed — pins the peer via <see cref="IdentityStore.Approve"/>.
///
/// One instance manages exactly one peer's ceremony, mirroring
/// <c>PeerControlChannel</c>'s "one instance, one peer" shape. This class
/// never races <c>PeerControlChannel.IdentityChanged</c>: that event only
/// ever fires for a connection with a specific expected approved peer (a
/// reconnect to someone already paired), while a pairing ceremony only ever
/// runs over a fresh <see cref="ConnectionTrust.PairingOnly"/> connection —
/// the two code paths are structurally disjoint, not coincidentally
/// non-overlapping.
///
/// Thread-safety: <see cref="OnFrameReceived"/> is expected to be invoked
/// from the control channel's receive-loop thread, while
/// <see cref="ConfirmLocalAsync"/>/<see cref="RejectLocalAsync"/>/
/// <see cref="Cancel"/>/<see cref="Tick"/> are expected to be invoked from
/// the UI thread. All ceremony-state mutation happens under a single gate,
/// never held across an await, mirroring <c>PeerControlChannel</c>'s own
/// discipline.
/// </summary>
public sealed class PairingCeremonyCoordinator
{
    readonly IPairingTransport _transport;
    readonly IdentityStore _identityStore;
    readonly SpkiPin _localSpki;
    readonly Guid _localPeerId;
    readonly SpkiPin _remoteSpki;
    readonly Guid _remotePeerIdHint;
    readonly byte[] _remoteCertificate;
    readonly PairingNonce _localNonce = PairingNonce.Generate();
    readonly DateTimeOffset _startedAt;
    readonly Func<DateTimeOffset> _clock;

    readonly object _gate = new();
    readonly PairingCeremony _ceremony = new();
    PairingPeerRecord? _remoteRecord;
    string? _code;
    bool _finalized;

    /// <summary>Raised once both nonces are known and the six-digit code has
    /// been computed, formatted "NNN NNN" for display.</summary>
    public event Action<string>? CodeReady;

    /// <summary>Raised exactly once, the instant both sides have confirmed
    /// and the peer has been pinned via <see cref="IdentityStore.Approve"/>.</summary>
    public event Action<ApprovedPeer>? Approved;

    /// <summary>Raised on an explicit reject, local or remote. No trust-state
    /// change has occurred by the time this fires.</summary>
    public event Action? Rejected;

    /// <summary>Raised when the 2-minute window elapses before both sides
    /// confirmed. No trust-state change has occurred by the time this
    /// fires.</summary>
    public event Action? Expired;

    public PairingCeremonyPhase Phase { get { lock (_gate) { return _ceremony.Phase; } } }

    public PairingCeremonyCoordinator(
        IPairingTransport transport,
        IdentityStore identityStore,
        SpkiPin localSpki,
        Guid localPeerId,
        SpkiPin remoteSpki,
        Guid remotePeerIdHint,
        byte[] remoteCertificate,
        DateTimeOffset startedAt,
        Func<DateTimeOffset>? clock = null)
    {
        _transport = transport;
        _identityStore = identityStore;
        _localSpki = localSpki;
        _localPeerId = localPeerId;
        _remoteSpki = remoteSpki;
        _remotePeerIdHint = remotePeerIdHint;
        _remoteCertificate = remoteCertificate;
        _startedAt = startedAt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        _transport.FrameReceived += OnFrameReceived;
    }

    /// <summary>Sends this side's nonce-exchange message. Call once, after
    /// construction.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var message = new PairingNonceMessage
        {
            Nonce = _localNonce,
            PeerId = _localPeerId,
            ProtocolVersion = Hello.CurrentProtocolVersion,
            Intent = PairingIntent.Pair,
        };
        return _transport.SendAsync(message.ToFrame(Guid.NewGuid()), cancellationToken);
    }

    void OnFrameReceived(ControlFrame frame)
    {
        switch (frame.Type)
        {
            case ControlMessageType.PairingNonce:
                HandleRemoteNonce(PairingFrameCodec.DecodeNonce(frame));
                break;
            case ControlMessageType.PairingConfirm:
                HandleConfirm(local: false);
                break;
            case ControlMessageType.PairingReject:
                HandleReject();
                break;
        }
    }

    void HandleRemoteNonce(PairingNonceMessage message)
    {
        string code;
        lock (_gate)
        {
            if (_ceremony.IsTerminal || _remoteRecord is not null) return;

            _remoteRecord = new PairingPeerRecord
            {
                Spki = _remoteSpki,
                Nonce = message.Nonce,
                PeerId = message.PeerId,
            };

            var localRecord = new PairingPeerRecord { Spki = _localSpki, Nonce = _localNonce, PeerId = _localPeerId };
            code = _code = PairingTranscript.ComputeCode(Hello.CurrentProtocolVersion, localRecord, _remoteRecord);
        }

        CodeReady?.Invoke(FormatCode(code));
    }

    /// <summary>The local user stamped a postcard "approved". Sends the
    /// local Confirm frame immediately and finalizes approval if the remote
    /// side had already confirmed.</summary>
    public async Task ConfirmLocalAsync(string friendlyName, CancellationToken cancellationToken)
    {
        bool shouldSend;
        lock (_gate)
        {
            shouldSend = !_ceremony.IsTerminal && _ceremony.Phase is not PairingCeremonyPhase.LocalConfirmed;
        }
        if (!shouldSend) return;

        await _transport.SendAsync(PairingFrameCodec.ConfirmFrame(Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        HandleConfirm(local: true, friendlyName);
    }

    void HandleConfirm(bool local, string? friendlyName = null)
    {
        ApprovedPeer? newlyApproved = null;
        lock (_gate)
        {
            var changed = local ? _ceremony.ConfirmLocal() : _ceremony.ConfirmRemote();
            if (!changed) return;

            if (_ceremony.Phase == PairingCeremonyPhase.Approved && !_finalized)
            {
                _finalized = true;
                newlyApproved = FinalizeApproval(friendlyName);
            }
        }

        if (newlyApproved is not null) Approved?.Invoke(newlyApproved);
    }

    ApprovedPeer FinalizeApproval(string? friendlyName)
    {
        var peer = new ApprovedPeer
        {
            PeerId = _remotePeerIdHint,
            FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? DefaultFriendlyName(_remotePeerIdHint) : friendlyName,
            SpkiSha256 = _remoteSpki,
            Certificate = _remoteCertificate,
            ApprovedAt = _clock(),
        };
        _identityStore.Approve(peer);
        _identityStore.CompletePairing(_remotePeerIdHint);
        return peer;
    }

    /// <summary>Sends an explicit, immediate reject (ADR-0002 — distinct
    /// from letting the window lapse).</summary>
    public async Task RejectLocalAsync(CancellationToken cancellationToken)
    {
        bool shouldSend;
        lock (_gate) { shouldSend = !_ceremony.IsTerminal; }
        if (!shouldSend) return;

        await _transport.SendAsync(PairingFrameCodec.RejectFrame(Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        HandleReject();
    }

    void HandleReject()
    {
        bool changed;
        lock (_gate) { changed = _ceremony.Reject(); }
        if (changed)
        {
            _identityStore.CompletePairing(_remotePeerIdHint);
            Rejected?.Invoke();
        }
    }

    /// <summary>Local-only cancellation (e.g. the user dismissed the
    /// postcard without stamping or tearing it). No frame is sent — this is
    /// deliberately distinct from <see cref="RejectLocalAsync"/>, which
    /// notifies the peer immediately. The remote side simply sees this
    /// ceremony expire after the normal 2-minute window if it doesn't
    /// separately reject.</summary>
    public void Cancel()
    {
        bool changed;
        lock (_gate) { changed = _ceremony.Cancel(); }
        if (changed) _identityStore.CompletePairing(_remotePeerIdHint);
    }

    /// <summary>Drive from a periodic timer (mirrors
    /// <c>PeerControlChannel.Tick</c>'s explicit-clock pattern). No-op unless
    /// the 2-minute window (<see cref="IdentityStore.PendingPairingExpiry"/>)
    /// has elapsed since the ceremony started.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (now - _startedAt < IdentityStore.PendingPairingExpiry) return;

        bool changed;
        lock (_gate) { changed = _ceremony.Expire(); }
        if (changed)
        {
            _identityStore.CompletePairing(_remotePeerIdHint);
            Expired?.Invoke();
        }
    }

    static string FormatCode(string sixDigits) => $"{sixDigits[..3]} {sixDigits[3..]}";

    /// <summary>Used only when the caller supplies no friendly name at
    /// confirmation time — a short, stable placeholder derived from the
    /// peer's non-secret display ID, renameable later from the Rolodex.</summary>
    static string DefaultFriendlyName(Guid peerId) => $"New peer {peerId.ToString("N")[..8]}";
}
