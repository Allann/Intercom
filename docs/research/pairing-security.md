# Local pairing, authentication, and encryption

## Decision

Use the cryptography already in Windows and .NET rather than creating a certificate authority, account system, or bespoke key-agreement protocol:

1. Each peer creates one long-lived ECDSA P-256 key and a self-signed X.509 identity certificate on first run.
2. Reliable peer connections use mutual TLS through .NET `SslStream`. The OS selects the strongest enabled TLS protocol; on the Windows 11 target this allows TLS 1.3.
3. During first contact, both peers exchange fresh random pairing nonces over the TLS connection and display the same six-digit short authentication string derived from the complete, canonically ordered pairing transcript. Both people must explicitly confirm that the numbers match.
4. Approval pins the peer identity by SHA-256 hash of its SubjectPublicKeyInfo (SPKI), not by friendly name, IP address, certificate serial number, or self-signed certificate subject.
5. Later TLS connections accept a self-signed certificate only when its SPKI hash matches the locally stored approved peer. A key change is a new peer and requires a new pairing ceremony.
6. Chat, presence, attention cards, acknowledgements, session control, group-floor control, and key-distribution messages stay inside the mutually authenticated `SslStream`.
7. Each live audio sender gets fresh random AES-256-GCM key material over the authenticated control channel. Audio datagrams are encrypted and authenticated independently with a per-stream nonce construction and replay window.
8. Protect the local identity private key and approved-peer registry with Windows DPAPI scoped to the current Windows user. No key or trust record leaves the device except the public certificate exchanged during pairing.

This satisfies the product meaning of an **approved peer**: discovery can advertise reachability and a proposed friendly name, but no protected operation is allowed until first-contact approval has completed and the public-key pin is stored.

## Stable peer identity

.NET's `CertificateRequest` supports an ECDSA signing key and can create a self-signed X.509 certificate without a CA. Generate the key with P-256, sign the certificate with SHA-256, include a random peer identifier in a non-security display/metadata field, and give the certificate a long but finite validity period. The cryptographic identity is the public key; certificate names and friendly names are untrusted presentation data. [Microsoft: `CertificateRequest`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.certificaterequest?view=net-10.0), [Microsoft: `CreateSelfSigned`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.certificaterequest.createselfsigned?view=net-10.0)

Fingerprint the DER-encoded SubjectPublicKeyInfo with SHA-256. Pinning SPKI permits a certificate to be reissued around the same identity key without silently changing the peer identity. Persist:

- protocol version;
- peer ID and friendly name (display/routing metadata);
- SHA-256 SPKI pin and public certificate;
- approval time and local contact association;
- revoked/forgotten state if retained for audit/debugging.

Do not treat a copied app-data folder as a second valid peer. The identity secret is tied to the Windows user's DPAPI credentials, so a different device or user cannot normally decrypt it.

## First-contact pairing ceremony

`SslStream` provides encrypted communication and certificate proof of private-key possession. It supports server authentication and optional client-certificate authentication, while its validation callbacks allow the app to implement public-key pinning instead of public-PKI hostname validation. Require the client certificate; reject a TLS connection that does not present one. [Microsoft: `SslStream`](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0), [Microsoft: `AuthenticateAsServerAsync`](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream.authenticateasserverasync?view=net-10.0), [Microsoft: `SslServerAuthenticationOptions`](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslserverauthenticationoptions?view=net-10.0)

For an unknown certificate, the validation callback may permit only a restricted **pairing connection**. It must not mark the peer approved or expose normal application commands. After TLS succeeds:

1. Each side generates a 128-bit random nonce with `RandomNumberGenerator` and sends it plus its claimed peer ID, protocol version, and pairing intent.
2. Construct one canonical transcript with a fixed protocol label, protocol version, both SPKI hashes, both nonces, and both peer IDs. Order the two peer records lexicographically by SPKI hash so both sides hash identical bytes regardless of which opened the connection. Use fixed-width fields or length-prefix every variable field.
3. Compute SHA-256 over the transcript. Convert the first 20 unbiased bits to a value from `000000` to `999999` using rejection sampling; do not simply apply modulo to an arbitrary integer.
4. Display the code prominently on both peers with device friendly names and ask the user to compare them. Approval requires confirmation on both devices; a one-sided confirmation expires without changing trust state.
5. Exchange confirmations inside the same TLS connection, then store the opposite SPKI pin. Close the restricted connection and reconnect under the normal approved-peer policy.

The fresh nonces prevent an attacker from precomputing a certificate that produces a chosen code. A man in the middle creates two different authenticated TLS connections and therefore different public-key/nonces transcripts; the humans detect it when the codes differ. A six-digit code has approximately 20 bits of online comparison strength, so rate-limit pairing attempts per source and globally, allow only one outstanding request per peer, expire requests after a few minutes, and show failures rather than silently retrying.

This is still a security-sensitive protocol. The pairing prototype ticket must implement fixed transcript test vectors and receive focused review before production use. If a well-maintained .NET library implementing an appropriate audited SAS/Noise pairing pattern is selected later, prefer it over maintaining this construction—but do not add a large WebRTC/account/certificate platform merely for pairing.

TLS key exporters are a standards-defined way to bind application values to a particular TLS session, and would strengthen the SAS binding if a supported target-framework API exposes them. RFC 9266 defines the TLS 1.3 channel binding as 32 bytes of exported keying material. Treat exporter inclusion as an implementation improvement, not an MVP dependency: the signed TLS identities plus fresh transcript nonces already make the displayed value session-specific. [RFC 9266: TLS 1.3 channel bindings](https://www.rfc-editor.org/rfc/rfc9266), [RFC 8446: TLS 1.3](https://www.rfc-editor.org/rfc/rfc8446)

## Approved connections

For an approved peer, certificate validation is fail closed:

- Compute the presented certificate's SPKI SHA-256 and compare it to the stored pin using constant-time equality.
- Require proof of the corresponding private key through the mutual TLS handshake.
- Reject expired/not-yet-valid certificates unless the implementation has an explicit, tested certificate-renewal path that keeps the same pinned key.
- Do not fall back from a pin mismatch to pairing in the same connection. Present “identity changed” locally and require an explicit forget/re-pair action.
- Do not accept by friendly name, discovered peer ID, IP address, or contact membership.

Allow the OS/.NET TLS stack to choose protocols and cipher suites instead of hardcoding a suite. Microsoft recommends letting the OS select the best protocol for `SslStream`; `SslProtocols.None` is the explicit form, while `SslProtocols.Default` can force obsolete versions in older APIs. [Microsoft: TLS best practices](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls)

Disable or carefully validate TLS session resumption initially. The implementation must prove that pin validation and peer association still occur for resumed sessions before enabling it. Set a dedicated ALPN value such as `intercom/1` to prevent accidentally speaking the protocol to an unrelated TLS service.

## Real-time audio datagrams

TCP/TLS is appropriate for ordered control and chat but retransmission can make late voice audio useless. Protect each audio datagram using .NET `AesGcm`:

- Generate a fresh 256-bit key and random 32-bit nonce prefix for each sender/stream epoch with `RandomNumberGenerator`.
- Send the key, prefix, stream ID, initial sequence number, and direction over the mutually authenticated control stream before accepting audio.
- Construct the 96-bit GCM nonce as `random stream prefix (32 bits) || monotonically increasing packet counter (64 bits)` in a specified byte order. Never reuse a `(key, nonce)` pair. End/rekey the stream before counter wrap and never reset a counter under the same key.
- Authenticate the unencrypted packet header (protocol version, conversation/session ID, stream ID, sequence/timestamp, codec flags) as associated data. Encrypt only the Opus payload and append a 128-bit authentication tag.
- Reject a datagram before decoding if its tag fails, stream/epoch is unknown, or sequence falls outside a sliding replay window. Track replay state independently per sender and stream epoch.
- Use distinct keys for each sending direction. For group fan-out, the simplest MVP rule is a separate audio key per receiving peer and separate encryption of the same encoded Opus packet for each peer. This avoids a shared group secret whose removal/rekey semantics are difficult in a serverless group.

`AesGcm` authenticates ciphertext and associated data and reports tag-validation failure on decryption. Its API also exposes permitted nonce and tag sizes; the implementation should assert the intended 12-byte nonce and 16-byte tag at startup/tests rather than assume platform behavior. [Microsoft: `AesGcm`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-10.0), [Microsoft: `AesGcm.Decrypt`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.decrypt?view=net-10.0)

Chat/control messages need no second application encryption layer inside TLS. Avoid long-lived pre-shared “family” keys: compromise of one peer would expose every peer and make member removal ambiguous.

## Local key storage

Use `ProtectedData.Protect`/`Unprotect` with `DataProtectionScope.CurrentUser` for:

- the identity certificate/private-key PKCS#8 or PFX payload;
- the approved-peer/trust registry;
- any pending certificate-renewal state.

Windows DPAPI uses the Windows user's credentials and requires no extra service or library. `CurrentUser` is preferable to `LocalMachine`: machine scope would allow other users on the PC to decrypt the material. Use a stable application-specific additional-entropy byte string as domain separation, restrict the containing file to app-local storage, write updates atomically, and never log decrypted keys or serialized trust records. [Microsoft: `ProtectedData`](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=windowsdesktop-10.0)

DPAPI does not protect against malware already executing as the same Windows user, nor against a person with control of that signed-in account. Those threats are outside the household MVP boundary and should be stated rather than implied away.

## Revocation, loss, and recovery

There is no central revocation service. “Forget peer” is local and immediate:

1. terminate its active TLS/audio sessions;
2. remove its SPKI pin/contact association locally;
3. delete pending session keys and replay state;
4. notify other devices only as an ordinary authenticated application event if product behavior calls for it.

The forgotten device remains approved on the other side until that user also forgets it. This asymmetry is inherent in peer-to-peer local trust and must be visible in the UI.

If the local identity key is lost or DPAPI decryption fails, generate a new peer identity and require re-pairing everywhere. Never silently replace the identity while retaining the old approved-peer records. Backing up/exporting identity keys is out of scope for the MVP.

## Required prototype and tests

The pairing prototype should validate the user ceremony as well as the primitives:

- two real Windows 11 PCs display the same code on a direct connection;
- a test MITM/two-connection harness produces different codes;
- swapped initiator/responder roles produce the same canonical transcript/code;
- transcript encoding has published byte-level test vectors;
- confirmation on only one side, expiry, cancellation, and repeated requests never create an approved peer;
- a pinned peer reconnects, while a different key with the same peer ID/name is rejected;
- corrupted DPAPI data fails closed and triggers identity-recovery guidance;
- modified, replayed, reordered, duplicated, and out-of-window audio packets are rejected or handled without nonce reuse;
- group fan-out uses independent receiver keys and removing a receiver stops new audio for it;
- logs and crash reports contain no private keys, session keys, plaintext audio, or full trust-store payloads.

Have the security protocol and test vectors reviewed separately from the UI prototype before implementation tickets treat the ceremony as final.

## New decisions and follow-up work

1. **Prototype the six-digit ceremony.** The cryptographic construction is viable, but the user must judge whether comparing/confirming a code on both PCs is understandable. Pairing approval remains unresolved until that prototype is accepted.
2. **Specify the wire transcript and packet formats.** Canonical byte encoding, ALPN, control message framing, audio nonce byte order, replay-window size, and key-epoch transitions need one versioned protocol specification with test vectors.
3. **Decide certificate lifetime and renewal.** Prefer renewing a certificate around the same identity key, but prototype/test what happens at expiry and how a same-key renewal is authenticated.
4. **Threat-model physical/signed-in access.** DPAPI CurrentUser is appropriate for MVP, but shared household Windows accounts affect who can approve/forget peers and access local history.
5. **Resolve multi-device contact trust separately.** Pairing approves peers, not a person or contact. Associating two approved peers with one contact is local routing metadata and must not make one peer capable of authorizing the other.

