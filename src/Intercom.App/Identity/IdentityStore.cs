using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Intercom.App.Identity;

/// <summary>
/// Owns this device's local identity, approved-peer registry, and in-progress
/// pairing ceremonies, all persisted with Windows DPAPI (CurrentUser scope)
/// per ADR-0002.
///
/// A missing, corrupted, or expired identity is treated the same way:
/// identity loss. A brand new identity is generated and the approved-peer
/// registry is wiped rather than carried forward — old approvals are
/// meaningless (and unsafe to keep) against a new identity.
///
/// "Corrupted" here means specifically undecryptable/unparseable data
/// (CryptographicException / JsonException / malformed payload) — that fails
/// closed and triggers recovery. A transient I/O failure (file share
/// violation, permissions) is a different problem and is NOT swallowed here;
/// it propagates, because silently treating "couldn't read the file right
/// now" the same as "this data is bad" would destroy a perfectly good
/// identity and force needless fleet-wide re-pairing.
/// </summary>
public sealed class IdentityStore
{
    const string IdentityEntropyLabel = "Intercom.Identity.v1";
    const string RegistryEntropyLabel = "Intercom.ApprovedPeers.v1";
    const string PendingPairingEntropyLabel = "Intercom.PendingPairings.v1";

    readonly string _identityPath;
    readonly string _registryPath;
    readonly string _pendingPairingPath;

    public LocalIdentity Identity { get; private set; } = null!;
    public ApprovedPeerRegistry Registry { get; } = new();
    public PendingPairingRegistry PendingPairings { get; } = new();

    /// <summary>True if this call generated a fresh identity because none
    /// existed yet, because the existing one was unreadable, or because it
    /// had expired.</summary>
    public bool IdentityWasRegenerated { get; private set; }

    /// <summary>True if the approved-peer registry file existed but could not
    /// be read (as opposed to legitimately not existing yet, or being wiped
    /// alongside a regenerated identity). Distinct from IdentityWasRegenerated
    /// so the two failure modes stay independently visible.</summary>
    public bool RegistryWasReset { get; private set; }

    public IdentityStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _identityPath = Path.Combine(dir, "identity.dat");
        _registryPath = Path.Combine(dir, "approved-peers.dat");
        _pendingPairingPath = Path.Combine(dir, "pending-pairings.dat");
    }

    public void LoadOrCreate()
    {
        var identityExistedBefore = File.Exists(_identityPath);
        var registryExistedBefore = File.Exists(_registryPath);

        var identity = TryLoadIdentity();
        if (identity is null)
        {
            IdentityWasRegenerated = identityExistedBefore;
            DeleteIfExists(_registryPath); // old approvals are meaningless against a new identity
            DeleteIfExists(_pendingPairingPath);
            identity = LocalIdentity.CreateNew();
            PersistIdentity(identity);
            PersistRegistry(Registry.Peers); // establish an empty registry file now, not lazily on first mutation
        }

        Identity = identity;

        if (!IdentityWasRegenerated)
        {
            // Only evaluate the registry independently when the identity itself
            // loaded fine — if the identity was just regenerated, the registry
            // was deliberately wiped above, not corrupted.
            var peers = TryLoadRegistry();
            if (peers is not null)
            {
                Registry.ReplaceAll(peers);
            }
            else if (registryExistedBefore)
            {
                RegistryWasReset = true;
            }

            var pending = TryLoadPendingPairings();
            if (pending is not null)
            {
                PendingPairings.ReplaceAll(pending);
            }
        }
    }

    public void SaveRegistry() => PersistRegistry(Registry.Peers);
    public void SavePendingPairings() => PersistPendingPairings(PendingPairings.Pending);

    LocalIdentity? TryLoadIdentity()
    {
        if (!File.Exists(_identityPath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_identityPath);
            var entropy = Encoding.UTF8.GetBytes(IdentityEntropyLabel);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);

            var dto = JsonSerializer.Deserialize<LocalIdentityDto>(plainBytes)
                ?? throw new InvalidDataException("Empty identity payload.");
            var certificate = X509CertificateLoader.LoadPkcs12(dto.Pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);

            // Expiry is handled identically to corruption/loss (ADR-0002): a
            // new identity, requiring re-pairing everywhere. NotAfter is local
            // time, per X509Certificate2's documented behavior.
            if (certificate.NotAfter <= DateTime.Now)
            {
                System.Diagnostics.Debug.WriteLine("Identity certificate has expired; regenerating.");
                return null;
            }

            return new LocalIdentity { PeerId = dto.PeerId, Certificate = certificate, CreatedAt = dto.CreatedAt };
        }
        catch (Exception ex) when (IsDataCorruption(ex))
        {
            // Fail closed: never log the payload, only that recovery is needed.
            System.Diagnostics.Debug.WriteLine($"Identity unreadable ({ex.GetType().Name}); regenerating.");
            return null;
        }
    }

    List<ApprovedPeer>? TryLoadRegistry()
    {
        if (!File.Exists(_registryPath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_registryPath);
            var entropy = Encoding.UTF8.GetBytes(RegistryEntropyLabel);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<ApprovedPeer>>(plainBytes);
        }
        catch (Exception ex) when (IsDataCorruption(ex))
        {
            System.Diagnostics.Debug.WriteLine($"Approved-peer registry unreadable ({ex.GetType().Name}); starting empty.");
            return null;
        }
    }

    List<PendingPairing>? TryLoadPendingPairings()
    {
        if (!File.Exists(_pendingPairingPath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_pendingPairingPath);
            var entropy = Encoding.UTF8.GetBytes(PendingPairingEntropyLabel);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<PendingPairing>>(plainBytes);
        }
        catch (Exception ex) when (IsDataCorruption(ex))
        {
            System.Diagnostics.Debug.WriteLine($"Pending-pairing state unreadable ({ex.GetType().Name}); starting empty.");
            return null;
        }
    }

    /// <summary>
    /// Only these mean "the data itself is bad" — undecryptable, unparseable,
    /// or structurally empty. Everything else (IOException, UnauthorizedAccessException,
    /// etc.) is a transient or environmental failure and must NOT be treated
    /// the same way, since that would destroy a perfectly good identity.
    /// </summary>
    static bool IsDataCorruption(Exception ex) =>
        ex is CryptographicException or JsonException or InvalidDataException or FormatException;

    void PersistIdentity(LocalIdentity identity)
    {
        var dto = new LocalIdentityDto
        {
            PeerId = identity.PeerId,
            Pfx = identity.Certificate.Export(X509ContentType.Pfx),
            CreatedAt = identity.CreatedAt,
        };
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var entropy = Encoding.UTF8.GetBytes(IdentityEntropyLabel);
        var protectedBytes = ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);
        AtomicWrite(_identityPath, protectedBytes);
    }

    void PersistRegistry(IReadOnlyList<ApprovedPeer> peers)
    {
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(peers);
        var entropy = Encoding.UTF8.GetBytes(RegistryEntropyLabel);
        var protectedBytes = ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);
        AtomicWrite(_registryPath, protectedBytes);
    }

    void PersistPendingPairings(IReadOnlyList<PendingPairing> pending)
    {
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(pending);
        var entropy = Encoding.UTF8.GetBytes(PendingPairingEntropyLabel);
        var protectedBytes = ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);
        AtomicWrite(_pendingPairingPath, protectedBytes);
    }

    static void AtomicWrite(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, path, overwrite: true);
    }

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    sealed class LocalIdentityDto
    {
        public required Guid PeerId { get; init; }
        public required byte[] Pfx { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }
}
