namespace Intercom.Discovery;

using Intercom.Diagnostics;
using Intercom.Identity;

/// <summary>
/// Owns local discovery end to end: registers this device's `_intercom._tcp.local`
/// advertisement and browses for peers on every eligible interface, re-evaluates
/// when interfaces change, sweeps TTL expiry, and exposes the resulting
/// "visible, unapproved" peer list (issue #20). Depends only on small seams
/// (<see cref="IDnsServiceDiscovery"/>, <see cref="INetworkInterfaceSnapshotProvider"/>,
/// <see cref="INetworkChangeNotifier"/>) plus the pure-policy <see cref="VisiblePeerList"/>,
/// mirroring how AppLifecycle composes IResidentWindow/ITrayIcon/IStartupService.
///
/// This class deliberately does nothing beyond discovery: no connection, no
/// TLS, no pairing, no approval — those are issue #21/#22. Consumers read
/// <see cref="VisiblePeers"/> and <see cref="VisiblePeersChanged"/>.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    static readonly TimeSpan DefaultExpirySweepInterval = TimeSpan.FromSeconds(5);

    readonly IDnsServiceDiscovery _dns;
    readonly INetworkInterfaceSnapshotProvider _interfaces;
    readonly INetworkChangeNotifier _networkChange;
    readonly VisiblePeerList _visiblePeers = new();
    readonly ServiceAdvertisement _advertisement;
    readonly Func<DateTimeOffset> _clock;
    readonly TimeSpan _expirySweepInterval;

    // Guards _active. ReEvaluate runs on whatever thread raises
    // NetworkChanged (not necessarily the thread that called Start), and
    // Dispose can race it if the app quits while a network-change event is
    // mid-flight — both mutate _active, so both need to serialize against
    // each other.
    readonly object _gate = new();
    readonly Dictionary<string, InterfaceRegistration> _active = new();
    Timer? _expiryTimer;
    bool _started;
    bool _disposed;

    public IReadOnlyList<VisiblePeer> VisiblePeers => _visiblePeers.Peers;

    /// <summary>Raised whenever the visible-peer list changes. No payload —
    /// re-read <see cref="VisiblePeers"/>, matching VisiblePeerList.Changed.</summary>
    public event Action? VisiblePeersChanged
    {
        add => _visiblePeers.Changed += value;
        remove => _visiblePeers.Changed -= value;
    }

    public DiscoveryService(
        IDnsServiceDiscovery dns,
        INetworkInterfaceSnapshotProvider interfaces,
        INetworkChangeNotifier networkChange,
        PeerIdHint localPeerIdHint,
        int controlChannelPort,
        SpkiPin? localSpki = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? expirySweepInterval = null)
    {
        _dns = dns;
        _interfaces = interfaces;
        _networkChange = networkChange;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expirySweepInterval = expirySweepInterval ?? DefaultExpirySweepInterval;

        // One control-channel port, advertised identically on every eligible
        // interface — there's exactly one TCP listener per peer (ADR-0001),
        // never a different port per NIC, so this never needs to vary.
        _advertisement = new ServiceAdvertisement
        {
            ProtocolVersion = DiscoveryProtocol.CurrentVersion,
            PeerIdHint = localPeerIdHint,
            Spki = localSpki,
            Port = controlChannelPort,
        };

        _networkChange.NetworkChanged += ReEvaluate;
    }

    /// <summary>Registers/browses every currently eligible interface and
    /// starts the TTL sweep timer. Call once.</summary>
    public void Start()
    {
        if (_started) throw new InvalidOperationException("DiscoveryService.Start must only be called once.");
        _started = true;

        ReEvaluate();
        _expiryTimer = new Timer(_ => SweepExpiry(_clock()), null, _expirySweepInterval, _expirySweepInterval);
    }

    /// <summary>Re-derives the eligible-interface set from the current OS
    /// snapshot and reconciles active registrations/browses against it:
    /// interfaces that appeared (new NIC, Wi-Fi reconnect) get registered and
    /// browsed; interfaces that disappeared or became ineligible (unplugged,
    /// went down) get torn down and their sightings dropped. Public so a host
    /// can also call it eagerly (e.g. right after onboarding grants network
    /// permission) instead of only reacting to NetworkChanged.</summary>
    public void ReEvaluate()
    {
        lock (_gate)
        {
            // A NetworkChanged event that was already in flight when Dispose
            // ran must not resurrect registrations after teardown.
            if (_disposed) return;

            var eligible = InterfaceEligibility.FilterEligible(_interfaces.GetCurrentInterfaces());
            DiagnosticLog.Current.Info(
                "discovery.interfaces",
                eligible.Count == 0
                    ? "No eligible multicast-capable LAN interfaces."
                    : string.Join("; ", eligible.Select(i => $"{i.Name} ipv4Index={i.Ipv4InterfaceIndex} ipv6Index={i.Ipv6InterfaceIndex} addresses={string.Join(',', i.UnicastAddresses)}")));
            var eligibleById = eligible.ToDictionary(i => i.Id);

            // Torn down first: an interface that dropped out shouldn't keep a
            // stale registration alive even briefly while we set up new ones.
            foreach (var staleId in _active.Keys.Where(id => !eligibleById.ContainsKey(id)).ToList())
            {
                TearDownLocked(staleId);
            }

            // An interface that's still eligible but whose bound addresses
            // changed (DHCP renewal, new IPv6 address, adapter re-index after
            // sleep/resume) is stale too: the old registration/browse were
            // bound to addresses that may no longer be valid, and other
            // peers need the refreshed record. Re-created below alongside
            // genuinely new interfaces.
            foreach (var (id, reg) in _active.ToList())
            {
                if (eligibleById.TryGetValue(id, out var current) && !HasSameAddresses(reg.Interface, current))
                {
                    TearDownLocked(id);
                }
            }

            foreach (var iface in eligible)
            {
                if (_active.ContainsKey(iface.Id)) continue; // already registered/browsing with current addresses

                IDisposable registration;
                IDisposable browse;
                try
                {
                    registration = _dns.Register(iface, _advertisement);
                    browse = _dns.Browse(iface, ObserveSignal);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Current.Error("discovery.interface-failed", $"interface={iface.Name} id={iface.Id}", ex);
                    throw;
                }

                _active[iface.Id] = new InterfaceRegistration(iface, registration, browse);
                DiagnosticLog.Current.Info("discovery.interface-active", $"interface={iface.Name} id={iface.Id}");
            }
        }
    }

    /// <summary>Every interface's browse callback funnels through here so
    /// there's exactly one place that drops our own advertisement: on a
    /// multi-homed PC (or simply the loopback-adjacent case of two
    /// interfaces both seeing this device's own mDNS traffic), a signal
    /// carrying our own <see cref="ServiceAdvertisement.PeerIdHint"/> is this
    /// device seeing itself, not another peer — it must never reach
    /// VisiblePeerList, whether that's a Seen or a Withdrawn signal.</summary>
    void ObserveSignal(DiscoverySignal signal)
    {
        if (signal.PeerIdHint == _advertisement.PeerIdHint) return;

        var wasVisible = _visiblePeers.Peers.Any(peer => peer.PeerIdHint == signal.PeerIdHint);
        _visiblePeers.Observe(signal, _clock());
        var isVisible = _visiblePeers.Peers.Any(peer => peer.PeerIdHint == signal.PeerIdHint);

        // DNS-SD refreshes a live record frequently. Logging every refresh
        // made the diagnostic file noisy enough to hide actual state changes.
        if (!wasVisible && isVisible)
            DiagnosticLog.Current.Info("discovery.peer-visible", $"peer={signal.PeerIdHint} interface={signal.InterfaceId}");
        else if (wasVisible && !isVisible)
            DiagnosticLog.Current.Info("discovery.peer-withdrawn", $"peer={signal.PeerIdHint} interface={signal.InterfaceId}");
    }

    /// <summary>Content comparison, not reference/record equality:
    /// LanInterface.UnicastAddresses is a freshly-built list on every OS
    /// snapshot, so relying on record-synthesized equality would report a
    /// "change" on every ReEvaluate even when nothing actually moved. Also
    /// compares the numeric adapter indexes DnsServiceRegister/Browse key
    /// off, since Windows can renumber those independently of the address
    /// set (e.g. across sleep/resume).</summary>
    static bool HasSameAddresses(LanInterface previous, LanInterface current) =>
        previous.Ipv4InterfaceIndex == current.Ipv4InterfaceIndex &&
        previous.Ipv6InterfaceIndex == current.Ipv6InterfaceIndex &&
        previous.UnicastAddresses.Select(a => a.ToString()).OrderBy(s => s, StringComparer.Ordinal)
            .SequenceEqual(current.UnicastAddresses.Select(a => a.ToString()).OrderBy(s => s, StringComparer.Ordinal));

    void SweepExpiry(DateTimeOffset now) => _visiblePeers.EvaluateExpiry(now);

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    void TearDownLocked(string interfaceId)
    {
        if (!_active.Remove(interfaceId, out var reg)) return;
        reg.Registration.Dispose();
        reg.Browse.Dispose();
        _visiblePeers.RemoveAllForInterface(interfaceId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _networkChange.NetworkChanged -= ReEvaluate;
            _expiryTimer?.Dispose();

            foreach (var id in _active.Keys.ToList()) TearDownLocked(id);
        }
    }

    sealed record InterfaceRegistration(LanInterface Interface, IDisposable Registration, IDisposable Browse);
}
