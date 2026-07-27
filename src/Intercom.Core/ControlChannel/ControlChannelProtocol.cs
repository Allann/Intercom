using System.Net.Security;

namespace Intercom.ControlChannel;

/// <summary>Wire-level constants shared by the connector and listener.</summary>
public static class ControlChannelProtocol
{
    /// <summary>Dedicated ALPN protocol id for the Intercom control channel
    /// (ADR-0001/issue #21 scope) — lets a handshake fail fast against
    /// anything that isn't speaking this protocol, and keeps this service
    /// distinguishable from any other TLS listener that might share a port
    /// range on the same machine.</summary>
    public static readonly SslApplicationProtocol AlpnId = new("intercom/1");
}
