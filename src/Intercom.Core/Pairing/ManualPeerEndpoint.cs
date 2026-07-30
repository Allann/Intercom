using System.Net;

namespace Intercom.Pairing;

/// <summary>A user-entered VPN endpoint. The original host is retained so it
/// can be corrected or retried without becoming identity evidence.</summary>
public sealed record ManualPeerEndpoint(string Host, int Port)
{
    public static ManualPeerEndpoint Parse(string value, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Enter an IP address or hostname.");
        if (defaultPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(defaultPort));

        var text = value.Trim();
        if (Uri.TryCreate($"tcp://{text}", UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
            return new ManualPeerEndpoint(uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port);

        throw new FormatException("Enter a valid IP address or hostname, optionally followed by a port.");
    }

    public async Task<IPEndPoint> ResolveAsync(CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(Host, out var literal)) return new IPEndPoint(literal, Port);
        var addresses = await Dns.GetHostAddressesAsync(Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.OrderBy(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault() ?? throw new InvalidOperationException($"Can’t reach a peer at {Host}.");
        return new IPEndPoint(address, Port);
    }

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
