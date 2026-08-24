using System.Net;
using Intercom.Identity;

namespace Intercom.Discovery;

internal sealed record ResolvedServiceData(
    IReadOnlyDictionary<string, string?> Properties,
    byte[]? Ipv4,
    byte[]? Ipv6,
    int Port);

internal static class ResolvedServiceReducer
{
    public static ResolvedService? Reduce(ResolvedServiceData data, int ipv6ScopeId)
    {
        if (!data.Properties.TryGetValue(DiscoveryProtocol.TxtKeyPeerIdHint, out var hint) ||
            string.IsNullOrWhiteSpace(hint)) return null;

        if (!TryReadSpki(data.Properties, out var spki)) return null;
        var version = ReadVersion(data.Properties);
        var ipv4 = ToAddress(data.Ipv4);
        var ipv6 = ToIpv6Address(data.Ipv6, ipv6ScopeId);

        return new ResolvedService(new PeerIdHint(hint), version, spki, ipv4, ipv6, data.Port);
    }

    static bool TryReadSpki(IReadOnlyDictionary<string, string?> properties, out SpkiPin? spki)
    {
        spki = null;
        if (!properties.TryGetValue(DiscoveryProtocol.TxtKeySpki, out var text) || text is null) return true;
        try { spki = new SpkiPin(Convert.FromHexString(text)); return true; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    static int ReadVersion(IReadOnlyDictionary<string, string?> properties) =>
        properties.TryGetValue(DiscoveryProtocol.TxtKeyVersion, out var text) && int.TryParse(text, out var value)
            ? value : 0;

    static IPAddress? ToAddress(byte[]? bytes) => bytes is null ? null : new IPAddress(bytes);

    static IPAddress? ToIpv6Address(byte[]? bytes, int scopeId)
    {
        if (bytes is null) return null;
        var address = new IPAddress(bytes);
        return address.IsIPv6LinkLocal ? new IPAddress(bytes, scopeId) : address;
    }
}

internal readonly record struct ResolvedService(
    PeerIdHint PeerIdHint,
    int ProtocolVersion,
    SpkiPin? Spki,
    IPAddress? Ipv4,
    IPAddress? Ipv6,
    int Port);
