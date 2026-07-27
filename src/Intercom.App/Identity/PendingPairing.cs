namespace Intercom.App.Identity;

/// <summary>
/// A pairing ceremony in progress with a peer, not yet approved. Persisted so
/// the 2-minute expiry and "one outstanding request per peer" rule (ADR-0002)
/// survive an app restart mid-ceremony. The actual ceremony wire protocol
/// (nonce exchange, transcript, SAS display) is issue #22's job — this is only
/// the storage primitive the ticket's scope requires now.
/// </summary>
public sealed record PendingPairing
{
    public required Guid PeerId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    public bool IsExpired(TimeSpan timeout, DateTimeOffset now) => now - StartedAt > timeout;
}
