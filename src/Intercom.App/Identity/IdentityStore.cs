using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Intercom.App.Identity;

/// <summary>
/// Owns this device's local identity and its approved-peer registry, both
/// persisted with Windows DPAPI (CurrentUser scope) per ADR-0002.
///
/// A missing OR corrupted identity blob is treated the same way: identity
/// loss. A brand new identity is generated and the approved-peer registry is
/// wiped rather than carried forward — old approvals are meaningless (and
/// unsafe to keep) against a new identity. This fails closed rather than
/// crashing: any read/decrypt/parse failure on either file is treated as
/// "not there," never surfaced as an unhandled exception.
/// </summary>
public sealed class IdentityStore
{
    const string IdentityEntropyLabel = "Intercom.Identity.v1";
    const string RegistryEntropyLabel = "Intercom.ApprovedPeers.v1";

    readonly string _identityPath;
    readonly string _registryPath;

    public LocalIdentity Identity { get; private set; } = null!;
    public ApprovedPeerRegistry Registry { get; } = new();

    /// <summary>True if this call generated a fresh identity because none
    /// existed yet, or because the existing one could not be read.</summary>
    public bool IdentityWasRegenerated { get; private set; }

    public IdentityStore(string? appDataDirectory = null)
    {
        var dir = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        Directory.CreateDirectory(dir);
        _identityPath = Path.Combine(dir, "identity.dat");
        _registryPath = Path.Combine(dir, "approved-peers.dat");
    }

    public void LoadOrCreate()
    {
        var identityExistedBefore = File.Exists(_identityPath);
        var identity = TryLoadIdentity();

        if (identity is null)
        {
            IdentityWasRegenerated = identityExistedBefore;
            DeleteIfExists(_registryPath); // never carry old approvals forward onto a new identity
            identity = LocalIdentity.CreateNew();
            PersistIdentity(identity);
        }

        Identity = identity;

        var peers = TryLoadRegistry();
        if (peers is not null)
        {
            Registry.ReplaceAll(peers);
        }
    }

    public void SaveRegistry() => PersistRegistry(Registry.Peers);

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

            return new LocalIdentity { PeerId = dto.PeerId, Certificate = certificate, CreatedAt = dto.CreatedAt };
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Approved-peer registry unreadable ({ex.GetType().Name}); starting empty.");
            return null;
        }
    }

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
