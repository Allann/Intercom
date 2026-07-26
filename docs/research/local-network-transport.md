# Local-network discovery and direct transport

Research for [issue #2](https://github.com/Allann/Intercom/issues/2). Sources are standards and first-party platform documentation.

## Decision

Use a deliberately split LAN architecture for the MVP:

1. **DNS-SD over multicast DNS for discovery.** Each peer advertises one `_intercom._tcp.local` service on its eligible LAN interfaces. The SRV record identifies the reliable-channel endpoint; TXT contains only a protocol version and a non-secret stable peer-identity hint. The friendly instance name is presentation data, not identity.
2. **One direct TCP connection per peer for reliable traffic.** A small length-prefixed application protocol carries pairing traffic, capability negotiation, presence, interruption mode, chat, attention cards and acknowledgements, voice-floor coordination, media endpoint negotiation, delivery receipts, heartbeats, and orderly errors. Simultaneous connection attempts are resolved deterministically from stable peer IDs so there is only one logical channel.
3. **Direct unicast UDP for live voice.** The control channel negotiates a bound media endpoint and session identifier. Opus mono voice packets carry sequence numbers and timestamps, are buffered briefly for jitter, and are never retransmitted after their playout deadline. Group push-to-talk replicates the current speaker's unicast packets directly to each participating peer.

This is peer-to-peer: every app can listen and initiate; there is no dedicated server role. A listening socket inside a peer is not the prohibited central-server architecture.

Do **not** use WebSockets for the peer protocol: they add an HTTP/WebSocket server handshake without solving discovery, peer trust, or real-time media. Do **not** adopt WebRTC for the LAN milestone: its internet NAT traversal machinery is unnecessary here. Do not use multicast for voice; unicast per recipient gives clearer loss accounting and avoids multicast-network variability.

## Why this fits

Multicast DNS performs DNS-like operations without a conventional DNS server and is explicitly link-local. DNS-SD uses PTR enumeration plus SRV/TXT records to turn a service type into named instances and endpoints, and the standards describe DNS-SD over mDNS as zero-configuration operation. Windows provides desktop DNS-SD APIs from Windows 10 onward: `DnsServiceRegister`, `DnsServiceBrowse`, and their related resolve/de-register operations. Using those OS APIs avoids shipping Bonjour or another separately installed discovery service. [RFC 6762](https://www.rfc-editor.org/rfc/rfc6762), [RFC 6763](https://www.rfc-editor.org/rfc/rfc6763), [Microsoft `DnsServiceBrowse`](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicebrowse)

TCP is a reliable, in-order byte stream, which suits durable state changes and chat, but the application still needs message framing, message IDs, delivery semantics, heartbeat/reconnection behavior, and idempotency. TCP itself does not provide peer identity or confidentiality. [RFC 9293](https://www.rfc-editor.org/rfc/rfc9293)

UDP avoids delaying current audio behind a lost packet. Opus is standardized for interactive speech and supports packet-loss concealment and optional in-band forward error correction. Start with mono 20 ms frames and tune bitrate/jitter from measurements rather than treating a bitrate as a protocol guarantee. The RTP payload specification supplies the relevant framing and loss/congestion constraints; the MVP can use a small application header with equivalent sequence/timestamp/session fields rather than implementing every RTP feature. [Opus codec specification, RFC 6716](https://www.rfc-editor.org/rfc/rfc6716), [RTP payload format for Opus, RFC 7587](https://www.rfc-editor.org/rfc/rfc7587)

Windows exposes TCP and UDP primitives through both Winsock/.NET and `Windows.Networking.Sockets`; the latter includes `StreamSocket`, `StreamSocketListener`, and `DatagramSocket`. The transport should sit behind an app-owned interface so a spike can select the least awkward C# implementation without changing the wire design. [Microsoft socket API overview](https://learn.microsoft.com/en-us/windows/apps/develop/networking/sockets), [Windows.Networking.Sockets](https://learn.microsoft.com/en-us/uwp/api/windows.networking.sockets)

## Discovery boundaries

- mDNS uses UDP 5353 and link-local multicast (IPv4 `224.0.0.251`, IPv6 `FF02::FB`). It normally does not cross routers or routed VPNs. That is the intended pure-local boundary; manual IP/hostname entry later bypasses discovery and connects to the same TCP endpoint.
- Discovery is convenience, never approval. DNS-SD records are visible and spoofable by devices on the link. Unknown results may appear in a pairing UI, but may not send chat, open audio, or trigger an attention card until approval succeeds. DNS-SD privacy guidance explicitly identifies identifying-information leakage, so TXT metadata must remain minimal. [RFC 8882](https://www.rfc-editor.org/rfc/rfc8882)
- Browse continuously rather than taking a one-time snapshot. Treat TTL expiry, goodbye records, connection heartbeat, and actual connect results as separate signals; discovery presence is not proof of availability.
- Preserve duplicate friendly names. Stable peer identity—not a DNS-SD instance label or IP address—disambiguates peers.
- Register and browse on each eligible active LAN interface. Initially exclude loopback and tunnel/VPN adapters from automatic discovery. Network changes must trigger re-evaluation and re-advertisement.
- Support IPv4 and IPv6. An IPv6 link-local destination needs its scope/interface ID, so endpoint records must retain interface context on multi-homed PCs. [Microsoft link-local IPv6 guidance](https://learn.microsoft.com/en-us/windows/win32/winsock/link-local-and-site-local-addresses-2)

## Ports and Windows Firewall

- UDP 5353 is fixed by mDNS.
- Bind the TCP control listener and one UDP media listener per peer. Advertise the TCP port in DNS-SD; negotiate the UDP port over TCP. Prefer stable configured ports or a narrow documented range so firewall behavior is testable; do not imply an unregistered private port is globally reserved.
- The packaged WinUI 3 desktop app normally runs medium-integrity full trust rather than AppContainer. If the package is changed to AppContainer, `privateNetworkClientServer` is the capability intended for inbound/outbound home and work network access. Microphone and background recording declarations are separate concerns. [Microsoft capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- Capability declaration is not a substitute for validating Windows Defender Firewall behavior. Installation/first run must be tested for inbound TCP and UDP on **Private** networks, denial, pre-existing block rules, non-admin users, and Public profiles. Any installer-authored rules should be app-scoped, Private-profile-only, and limited to local subnet/required ports. Windows documents that explicit block rules take precedence over conflicting allow rules. [Microsoft firewall rules](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules)

The exact MSIX/firewall integration is an implementation spike, not resolved by the transport choice.

## Minimal wire responsibilities

The reliable protocol should define from its first version:

- protocol version and capability negotiation;
- stable peer ID and authenticated identity placeholder;
- message type, unique message ID, correlation ID, and bounded payload length;
- explicit delivered/acknowledged states where the domain distinguishes them;
- heartbeat, timeout, reconnect, duplicate suppression, and graceful shutdown;
- a session token binding each UDP voice packet to an authorized live voice session;
- limits and rate controls before decoding untrusted input.

Voice packets need at least protocol/session ID, monotonically wrapping sequence number, capture timestamp, codec configuration identifier, and encoded payload. Receivers discard unknown/expired sessions, tolerate reordering within a small window, conceal loss, and bound the jitter buffer. UDP provides neither congestion control nor delivery guarantees, so stop or reduce media when sustained loss indicates an unusable path.

## Security boundary left open

Neither DNS-SD, plain TCP, nor plain UDP authenticates or encrypts the family traffic. The pairing-security prototype must choose how the displayed verification ceremony binds a stable cryptographic peer identity, and how that identity protects both reliable and media traffic. Discovery metadata must be treated as attacker-controlled regardless of that later choice.

QUIC is a credible later alternative because it combines TLS 1.3 with multiplexed reliable streams, and .NET 9 marks `System.Net.Quic` stable on Windows 11 with MsQuic shipped in the runtime. It is not the default MVP recommendation because the public .NET surface and datagram/media fit still require a spike, while the pairing certificate ceremony remains unresolved. [Microsoft .NET QUIC overview](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)

## Required prototype/test tickets

1. **DNS-SD + firewall spike:** two packaged WinUI 3 peers advertise, discover, connect over TCP, and exchange UDP on clean Windows 11 machines without a special install; document prompts/rules on Private and Public profiles.
2. **Multi-interface resilience:** IPv4-only, IPv6 dual-stack, simultaneous Ethernet/Wi-Fi, network profile changes, sleep/resume, address changes, duplicate names, multicast-disabled/client-isolated Wi-Fi, and app restart.
3. **Audio transport spike:** AudioGraph capture/render through Opus over UDP with measured mouth-to-ear latency, jitter, packet loss/reordering, device changes, and group fan-out. Windows recommends AudioGraph for new low-latency scenarios and exposes a lowest-latency quantum mode. [Microsoft low-latency audio guidance](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio), [AudioGraph sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-universal-samples/audiocreation/)
4. **Pairing/security prototype:** decide the verification experience and protection for TCP and UDP before accepting any wire protocol as production-ready.

## Resulting decisions

- Automatic discovery is DNS-SD/mDNS and intentionally local-link only.
- Reliable peer state and messaging use a direct framed TCP channel.
- Live voice uses negotiated direct unicast UDP with Opus and sequence/timestamp/jitter handling.
- Discovery never establishes trust and carries no sensitive family/contact data.
- Manual VPN addressing later reuses the direct transport and does not depend on multicast discovery.
