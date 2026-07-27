namespace Intercom.ControlChannel;

/// <summary>
/// Accepts inbound mutually-authenticated TLS connections on this device's
/// control-channel port. One listener serves every peer — routing an
/// accepted <see cref="IPeerTransportConnection"/> to the right
/// <see cref="PeerControlChannel"/> instance (matching it by the presented
/// certificate's SPKI hash, or handing an unrecognized one to a pairing
/// flow) is app-shell wiring outside this ticket's scope; see the module's
/// report for what's built here versus what a caller still has to wire up.
/// </summary>
public interface IPeerTransportListener : IDisposable
{
    /// <summary>Starts accepting. Call once.</summary>
    void Start();

    /// <summary>Raised once per accepted, TLS-handshake-complete inbound
    /// connection. A handshake that fails (bad ALPN, no client cert offered,
    /// TLS-level error) never raises this — there is nothing to hand the
    /// caller for it.</summary>
    event Action<IPeerTransportConnection>? ConnectionAccepted;
}
