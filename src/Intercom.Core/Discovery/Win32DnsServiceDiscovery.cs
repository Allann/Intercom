using System.Net;
using System.Runtime.InteropServices;
using Intercom.Diagnostics;
using Intercom.Identity;

namespace Intercom.Discovery;

/// <summary>
/// Real Windows implementation of <see cref="IDnsServiceDiscovery"/> using
/// DnsServiceRegister/DnsServiceBrowse/DnsServiceResolve from dnsapi.dll
/// (available since Windows 10; no managed wrapper exists in .NET, hence the
/// P/Invoke below).
///
/// tests/Intercom.Discovery.Integration exercises this class across two real
/// processes on a Windows host and verifies bidirectional register/browse/
/// resolve behavior. That harness cannot replace the two-PC firewall/network
/// acceptance pass called out in prototypes/14-lan-resilience/README.md, but
/// it does keep the native marshaling path under repeatable runtime coverage.
///
/// DnsServiceBrowse only ever delivers PTR add/remove notifications for the
/// service type — it does not resolve an instance's own SRV/TXT/address
/// records inline (see <see cref="ServiceBrowse"/>'s remarks), so every
/// newly-seen instance name is resolved separately via DnsServiceResolve,
/// which hands back one fully-populated DNS_SERVICE_INSTANCE rather than a
/// DNS_RECORD list. DNS_RECORD itself is a large tagged union in native code
/// (https://learn.microsoft.com/windows/win32/api/windns/ns-windns-dns_recordw);
/// rather than declare the entire union in C#, the browse side reads only the
/// fixed header plus the PTR payload it needs by pointer arithmetic from
/// documented x64 struct offsets — see <see cref="DnsRecordHeaderSize"/>.
/// This targets x64 (the only architecture this app ships as), and is the
/// one place in the module where "policy" and "native marshaling" are
/// unavoidably tangled together, which is exactly why the rest of the module
/// talks to this class only through <see cref="IDnsServiceDiscovery"/>.
/// </summary>
public sealed class Win32DnsServiceDiscovery : IDnsServiceDiscovery
{
    public IDisposable Register(LanInterface iface, ServiceAdvertisement advertisement)
    {
        var interfaceIndex = iface.Ipv4InterfaceIndex ?? iface.Ipv6InterfaceIndex ?? 0;
        var hostName = Dns.GetHostName() + "." + DiscoveryProtocol.Domain;
        var instanceName = $"Intercom-{advertisement.PeerIdHint}.{DiscoveryProtocol.ServiceType}.{DiscoveryProtocol.Domain}";

        var ipv4 = iface.UnicastAddresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var ipv6 = iface.UnicastAddresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

        return new ServiceRegistration(instanceName, hostName, ipv4, ipv6, advertisement.Port, advertisement.PeerIdHint, advertisement.Spki, interfaceIndex);
    }

    public IDisposable Browse(LanInterface iface, Action<DiscoverySignal> onSignal)
    {
        var interfaceIndex = iface.Ipv4InterfaceIndex ?? iface.Ipv6InterfaceIndex ?? 0;
        // Deliberately not the same value as interfaceIndex above: LanInterface
        // documents these as independent (Windows can report different index
        // values per family for the same adapter), and an IPv6 ScopeId must be
        // the IPv6-specific one, not whichever family happened to be present
        // first. Falls back to the general index only if this interface has no
        // IPv6 index at all — better than 0 (definitely wrong), still
        // potentially wrong if they genuinely differ and IPv6 isn't tracked,
        // but that combination shouldn't produce a resolvable IPv6 address in
        // the first place.
        var ipv6ScopeId = iface.Ipv6InterfaceIndex ?? interfaceIndex;
        var queryName = $"{DiscoveryProtocol.ServiceType}.{DiscoveryProtocol.Domain}";
        return new ServiceBrowse(queryName, iface.Id, interfaceIndex, ipv6ScopeId, onSignal);
    }

    // ---- DNS_RECORD header (x64) ----
    // pNext(8) + pName(8) + wType(2) + wDataLength(2) + Flags(4) + dwTtl(4) + dwReserved(4) = 32,
    // the union payload (Data) starts immediately after.
    const int DnsRecordHeaderSize = 32;
    const ushort DnsTypePtr = 12;

    const uint DnsQueryResultsFalse = 0;
    const uint ErrorCancelled = 1223;
    const uint DnsRequestPending = 9506; // WSA_IO_PENDING-equivalent DNS_STATUS for a pending async request
    const int DnsFreeRecordList = 1; // DNS_FREE_TYPE.DnsFreeRecordList

    /// <summary>Live registration for one interface. Disposing calls
    /// DnsServiceDeRegister so the goodbye record goes out.
    ///
    /// Both DnsServiceRegister and DnsServiceDeRegister are asynchronous and
    /// share the same completion-callback shape (windns.h: "The callback
    /// will be invoked when the deregistration is completed, with a copy of
    /// the DNS_SERVICE_INSTANCE structure that was passed to
    /// DnsServiceRegister"). Two things follow from that:
    /// 1. Our own <see cref="_instance"/> buffer (the pServiceInstance we
    ///    handed to DnsServiceRegister, and which DnsServiceDeRegister reads
    ///    again via the same _request) must stay alive until BOTH the
    ///    register call's completion and the deregister call's completion
    ///    have actually fired — freeing it as soon as Dispose() returns
    ///    (the old behavior) races the OS still reading it.
    /// 2. Each completion callback also hands back its own, separately
    ///    OS-allocated DNS_SERVICE_INSTANCE ("If not nullptr, then you are
    ///    responsible for freeing the data using DnsServiceFreeInstance") —
    ///    a completely different pointer from our own buffer, and one this
    ///    class previously never freed on either callback.
    ///
    /// A third thing that isn't about the native buffer at all:
    /// <see cref="Marshal.GetFunctionPointerForDelegate"/> hands the OS a raw
    /// function pointer, which does not keep the managed delegate (or its
    /// target — this object) alive. Once nothing external references a
    /// disposed ServiceRegistration (its owner, DiscoveryService, drops its
    /// reference right after calling Dispose()), the GC is free to collect
    /// it — and the delegate/JIT thunk backing that function pointer along
    /// with it — while DnsServiceDeRegister's completion is still pending.
    /// <see cref="_selfHandle"/> is an explicit GC root that keeps this
    /// object (and therefore <see cref="_callback"/>) alive independent of
    /// any managed reference, freed at the exact same point <see
    /// cref="_instance"/> is (both completions have fired) — the one point
    /// we know for certain the OS will never call back into this object
    /// again, per the singular ("registration has completed") phrasing of
    /// DNS_SERVICE_REGISTER_COMPLETE's own docs (unlike DnsServiceResolve's
    /// "invoked for each result" — see PendingResolve for why that one needs
    /// a different strategy).
    /// </summary>
    sealed class ServiceRegistration : IDisposable
    {
        readonly DNS_SERVICE_REGISTER_COMPLETE _callback; // kept alive for the lifetime of the native request
        DNS_SERVICE_REGISTER_REQUEST _request;
        GCHandle _selfHandle;

        readonly object _gate = new();
        NativeServiceInstance? _instance; // null once freed
        bool _deregisterIssued;
        int _expectedCompletions = 1; // register only, until Dispose() bumps this to 2
        int _completionsSeen;

        public ServiceRegistration(string instanceName, string hostName, IPAddress? ipv4, IPAddress? ipv6, int port, PeerIdHint peerIdHint, SpkiPin? spki, int interfaceIndex)
        {
            _callback = OnCompletion;
            _selfHandle = GCHandle.Alloc(this);
            var txt = new Dictionary<string, string>
            {
                [DiscoveryProtocol.TxtKeyVersion] = DiscoveryProtocol.CurrentVersion.ToString(),
                [DiscoveryProtocol.TxtKeyPeerIdHint] = peerIdHint.Value,
            };
            if (spki is { } pin) txt[DiscoveryProtocol.TxtKeySpki] = pin.ToString();

            _instance = new NativeServiceInstance(
                instanceName,
                hostName,
                ipv4,
                ipv6,
                (ushort)port,
                txt);

            _request = new DNS_SERVICE_REGISTER_REQUEST
            {
                Version = 1,
                InterfaceIndex = (uint)interfaceIndex,
                pServiceInstance = _instance.Pointer,
                pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
                pQueryContext = IntPtr.Zero,
                hCredentials = IntPtr.Zero,
                unicastEnabled = false,
            };

            var status = DnsServiceRegister(ref _request, IntPtr.Zero);
            if (status != DnsQueryResultsFalse && status != DnsRequestPending)
            {
                // The request was never actually queued, so no completion
                // callback is ever coming for it — safe, and necessary, to
                // free our buffer (and stop self-rooting) right here.
                _instance.Dispose();
                _instance = null;
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                throw new InvalidOperationException($"DnsServiceRegister failed with status {status}.");
            }
        }

        /// <summary>Shared by the register and deregister completions —
        /// windns.h reuses the same callback shape for both, distinguished
        /// here only by how many completions we've seen relative to how many
        /// we expect (see the class remarks).</summary>
        void OnCompletion(uint status, IntPtr context, IntPtr instance)
        {
            if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
            if (status != DnsQueryResultsFalse)
            {
                System.Diagnostics.Debug.WriteLine($"DnsService register/deregister completion reported status {status}.");
            }

            lock (_gate)
            {
                _completionsSeen++;
                TryFreeOwnBufferLocked();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_deregisterIssued) return;
                _deregisterIssued = true;
                _expectedCompletions = 2;
            }

            var status = DnsServiceDeRegister(ref _request, IntPtr.Zero);

            lock (_gate)
            {
                if (status != DnsQueryResultsFalse && status != DnsRequestPending)
                {
                    // Deregistration itself never started, so its completion
                    // is never coming either — don't wait for a callback
                    // that will never arrive.
                    System.Diagnostics.Debug.WriteLine($"DnsServiceDeRegister failed to start with status {status}.");
                    _expectedCompletions = 1;
                }
                TryFreeOwnBufferLocked();
            }
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        void TryFreeOwnBufferLocked()
        {
            if (_instance is null) return; // already freed
            if (!_deregisterIssued) return; // still registered — keep the buffer alive regardless of completions so far
            if (_completionsSeen < _expectedCompletions) return; // still waiting on a completion

            _instance.Dispose();
            _instance = null;
            if (_selfHandle.IsAllocated) _selfHandle.Free(); // no further callback will ever arrive — safe to stop rooting ourselves
        }
    }

    /// <summary>Live browse subscription for one interface. DnsServiceBrowse
    /// itself only ever delivers PTR add/remove notifications for the
    /// service type (see <see cref="DnsRecordParser.ParsePtrEvents"/>) — it
    /// does not resolve an instance's SRV/TXT/address records inline, so
    /// every newly-seen instance name triggers a resolve subscription (see
    /// <see cref="PendingResolve"/>) to fetch those (docs: "In contrast to
    /// DnsServiceBrowse — which returns the service name as a minimum —
    /// DnsServiceResolve can be used to retrieve additional information").
    /// The resolved TXT/address set is cached per instance name so a later
    /// PTR re-announcement (mDNS responders refresh periodically) only
    /// refreshes the TTL rather than re-resolving.
    ///
    /// <b>GC rooting.</b> Like <see cref="ServiceRegistration"/>,
    /// <see cref="Marshal.GetFunctionPointerForDelegate"/> does not keep
    /// <see cref="_browseCallback"/> (or this object) alive on its own — see
    /// <see cref="_selfHandle"/>. Unlike registration, there's no documented
    /// single point at which we're guaranteed no further browse callback
    /// (including a possible final one after DnsServiceBrowseCancel) will
    /// ever arrive, so <see cref="_selfHandle"/> and <see
    /// cref="_queryNameHandle"/> are freed together, best-effort, from the
    /// first <see cref="OnBrowseCallback"/> invocation observed after
    /// Dispose() — see <see cref="FreeSelfRootingLocked"/>. If cancellation
    /// happens to produce no further callback at all, this leaks one pinned
    /// string and one GCHandle for the process's remaining lifetime — this
    /// is a per-interface object (torn down only on interface loss/app
    /// quit), so the worst case is small and bounded, and preferred over the
    /// GC collecting a delegate the OS still holds a raw pointer to.</summary>
    sealed class ServiceBrowse : IDisposable
    {
        readonly DNS_SERVICE_BROWSE_CALLBACK _browseCallback; // kept alive for the lifetime of the native request
        readonly DNS_SERVICE_RESOLVE_COMPLETE _resolveCallback; // kept alive for the lifetime of every resolve request
        readonly Action<DiscoverySignal> _onSignal;
        readonly string _interfaceId;
        readonly int _interfaceIndex;
        readonly int _ipv6ScopeId;
        GCHandle _queryNameHandle;
        GCHandle _selfHandle;

        // Guards _pendingResolves/_resolved/_disposed/handle-freeing. The
        // browse callback and every outstanding resolve's completion
        // callback can each fire on their own native thread pool thread,
        // concurrently with each other and with Dispose (called from the
        // UI/app-quit thread).
        readonly object _gate = new();
        readonly Dictionary<string, PendingResolve> _pendingResolves = new();
        readonly Dictionary<string, CachedInstance> _resolved = new();
        DNS_SERVICE_BROWSE_REQUEST _request;
        DNS_SERVICE_CANCEL _cancel;
        bool _disposed;
        bool _selfRootingFreed;

        public ServiceBrowse(string queryName, string interfaceId, int interfaceIndex, int ipv6ScopeId, Action<DiscoverySignal> onSignal)
        {
            _onSignal = onSignal;
            _interfaceId = interfaceId;
            _interfaceIndex = interfaceIndex;
            _ipv6ScopeId = ipv6ScopeId;
            _browseCallback = OnBrowseCallback;
            _resolveCallback = OnResolveComplete;
            _queryNameHandle = GCHandle.Alloc(queryName, GCHandleType.Pinned);
            _selfHandle = GCHandle.Alloc(this);

            _request = new DNS_SERVICE_BROWSE_REQUEST
            {
                Version = 1,
                InterfaceIndex = (uint)interfaceIndex,
                QueryName = _queryNameHandle.AddrOfPinnedObject(),
                pBrowseCallback = Marshal.GetFunctionPointerForDelegate(_browseCallback),
                pQueryContext = IntPtr.Zero,
            };

            _cancel = default;
            var status = DnsServiceBrowse(ref _request, ref _cancel);
            if (status != DnsQueryResultsFalse && status != DnsRequestPending)
            {
                // Never queued — no callback is ever coming for it.
                _queryNameHandle.Free();
                _selfHandle.Free();
                throw new InvalidOperationException($"DnsServiceBrowse failed with status {status}.");
            }
        }

        void OnBrowseCallback(uint status, IntPtr context, IntPtr dnsRecord)
        {
            bool disposedAtEntry;
            lock (_gate) { disposedAtEntry = _disposed; }

            if (status != DnsQueryResultsFalse || dnsRecord == IntPtr.Zero)
            {
                // Disposed *and* this looks like the post-cancellation
                // notification (no record list, or an error/cancelled
                // status) — treat it as the final callback we've been
                // waiting for.
                if (disposedAtEntry) { lock (_gate) FreeSelfRootingLocked(); }
                return;
            }

            try
            {
                foreach (var (instanceName, ttl) in DnsRecordParser.ParsePtrEvents(dnsRecord))
                {
                    if (ttl == 0) HandleGoodbye(instanceName);
                    else HandleAdd(instanceName, ttl);
                }
            }
            catch (Exception ex)
            {
                // Fail closed for this one notification, never crash the
                // native callback thread — an unparsed record is treated as
                // "nothing observed", not as a peer.
                DiagnosticLog.Current.Error("discovery.browse-callback-failed", $"interface={_interfaceId}", ex);
            }
            finally
            {
                // Ours to free per DnsServiceBrowseCallback's documented
                // contract ("you are responsible for freeing the returned RR
                // sets using DnsRecordListFree") — otherwise every single
                // browse notification for the lifetime of the app leaks the
                // native record list it carried.
                DnsRecordListFree(dnsRecord, DnsFreeRecordList);

                // A disposed browse can still legitimately receive one more
                // real notification (e.g. a goodbye that was already in
                // flight) before the OS stops calling back entirely — this
                // is that "maybe one more, then done" point, same reasoning
                // as the error/no-record branch above.
                if (disposedAtEntry) { lock (_gate) FreeSelfRootingLocked(); }
            }
        }

        /// <summary>Caller must hold <see cref="_gate"/>. Idempotent —
        /// safe to call from more than one post-Dispose callback
        /// invocation.</summary>
        void FreeSelfRootingLocked()
        {
            if (_selfRootingFreed) return;
            _selfRootingFreed = true;
            if (_queryNameHandle.IsAllocated) _queryNameHandle.Free();
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }

        void HandleAdd(string instanceName, uint ttl)
        {
            CachedInstance? refresh = null;

            lock (_gate)
            {
                if (_disposed) return;

                if (_resolved.TryGetValue(instanceName, out var cached))
                {
                    refresh = cached; // re-announcement of an already-resolved instance — just a TTL refresh
                }
                else if (!_pendingResolves.ContainsKey(instanceName)) // else: resolve already in flight for this instance
                {
                    try
                    {
                        _pendingResolves[instanceName] = new PendingResolve(instanceName, ttl, _interfaceIndex, _resolveCallback);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Current.Error("discovery.resolve-start-failed", $"instance={instanceName} interface={_interfaceId}", ex);
                    }
                }
            }

            // Emitted outside the lock: _onSignal ultimately reaches
            // VisiblePeerList.Observe, which takes its own lock — never call
            // out to another component's lock while holding this one.
            if (refresh is not null) EmitSeenSignals(refresh.Value, ttl);
        }

        void HandleGoodbye(string instanceName)
        {
            CachedInstance? removed = null;
            PendingResolve? cancelling = null;

            lock (_gate)
            {
                if (_disposed) return;

                if (_pendingResolves.Remove(instanceName, out var pending)) cancelling = pending;

                // A goodbye can only be reported as a Withdrawn signal (which
                // requires a PeerIdHint) if this instance was ever
                // successfully resolved. A goodbye for a still-unresolved
                // instance is simply dropped — nothing was ever announced as
                // visible for it.
                if (_resolved.Remove(instanceName, out var cached)) removed = cached;
            }

            // Cancellation must happen outside _gate: DnsServiceResolveCancel
            // is not documented as non-blocking, and if it blocks until an
            // in-flight completion has finished running, that completion's
            // own bookkeeping needs this same lock — holding it here would
            // deadlock the two threads against each other.
            cancelling?.Dispose();

            if (removed is not null)
            {
                EmitSignal(new DiscoverySignal.Withdrawn
                {
                    PeerIdHint = removed.Value.PeerIdHint,
                    InterfaceId = _interfaceId,
                    ObservedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        /// <summary>windns.h documents DnsServiceResolve as "the resolve
        /// callback will be invoked for each result" (plural) — unlike
        /// registration's single "registration has completed" callback,
        /// resolve behaves like an ongoing mDNS subscription and can call
        /// back more than once for the same outstanding request (e.g. a
        /// later responder answering with updated address/TXT data). This
        /// method is therefore written to be invoked repeatedly for the same
        /// PendingResolve, and treats every valid invocation as a legitimate
        /// refresh rather than "the" one-and-only result:
        /// - <see cref="_pendingResolves"/> is never touched here — a
        ///   PendingResolve is only ever removed by HandleGoodbye/Dispose,
        ///   the two places that actually decide "we don't want this
        ///   instance anymore".
        /// - <see cref="PendingResolve.FreeHandles"/> is only called once
        ///   this specific invocation observes that cancellation was already
        ///   requested (<see cref="PendingResolve.WasCancelled"/>) — i.e.
        ///   this is being invoked *after* HandleGoodbye/Dispose already
        ///   gave up on it, not merely because it happens to be the first
        ///   result. FreeHandles' own Interlocked guard makes it safe if
        ///   more post-cancellation invocations still trickle in afterward.</summary>
        void OnResolveComplete(uint status, IntPtr contextPtr, IntPtr pInstance)
        {
            PendingResolve pending;
            try
            {
                // The context handle's target IS the owning PendingResolve
                // (rather than a separate small DTO) specifically so this
                // line never depends on _pendingResolves still containing an
                // entry for it — see PendingResolve's remarks. The try/catch
                // is defense in depth against the one residual race
                // DnsServiceResolveCancel's undocumented exact semantics
                // can't fully rule out: a callback invocation arriving after
                // FreeHandles() already ran for this exact contextPtr value.
                // Two distinct failure shapes are possible if that happens:
                // .Target itself throws (the handle slot is simply empty), or
                // — much rarer — the slot was already reused by an unrelated
                // GCHandle.Alloc elsewhere by the time this stale callback
                // arrives, in which case .Target succeeds but the cast below
                // fails. Both are "this callback is stale", not a crash.
                pending = (PendingResolve)GCHandle.FromIntPtr(contextPtr).Target!;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException)
            {
                // The handle behind contextPtr is no longer valid for this
                // request — a stale callback for an already-finalized one.
                // Nothing to free, nothing to report; drop it.
                return;
            }

            var instanceName = pending.InstanceName;
            bool wasDisposed;
            lock (_gate) { wasDisposed = _disposed; }

            if (pending.WasCancelled) pending.FreeHandles(); // this browse/instance no longer wants results — safe to reclaim now

            CachedInstance? resolved = null;
            try
            {
                if (status != DnsQueryResultsFalse || pInstance == IntPtr.Zero)
                {
                    if (status != DnsQueryResultsFalse && status != ErrorCancelled)
                        DiagnosticLog.Current.Warning("discovery.resolve-status", $"status={status} instance={instanceName} interface={_interfaceId}");
                    return;
                }

                try
                {
                    // The browse this resolve belongs to was torn down while
                    // the resolve was still in flight — there's no one left
                    // to notify. Still fall through to the finally below so
                    // the native instance gets freed either way.
                    if (wasDisposed || pending.WasCancelled) return;

                    var cached = DnsRecordParser.ParseResolvedInstance(pInstance, _ipv6ScopeId);
                    if (cached is null) return; // malformed/foreign instance on the same service type — never guess an identity

                    lock (_gate)
                    {
                        if (_disposed || !_pendingResolves.ContainsKey(instanceName)) return; // withdrawn/torn down since we last checked
                        _resolved[instanceName] = cached.Value;
                    }
                    resolved = cached;
                }
                finally
                {
                    // Ours to free per DNS_SERVICE_RESOLVE_COMPLETE's
                    // documented contract regardless of whether we ended up
                    // using it.
                    DnsServiceFreeInstance(pInstance);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Current.Error("discovery.resolve-callback-failed", $"instance={instanceName} interface={_interfaceId}", ex);
            }

            if (resolved is not null) EmitSeenSignals(resolved.Value, pending.Ttl);
        }

        /// <summary>Emits one Seen signal per address family the instance
        /// actually resolved (docs/research/local-network-transport.md:
        /// "Support IPv4 and IPv6") rather than picking just one.</summary>
        void EmitSeenSignals(CachedInstance cached, uint ttl)
        {
            var now = DateTimeOffset.UtcNow;
            var ttlSpan = TimeSpan.FromSeconds(ttl);

            DiscoverySignal.Seen BuildSeen(IPAddress address) => new()
            {
                PeerIdHint = cached.PeerIdHint,
                InterfaceId = _interfaceId,
                ObservedAt = now,
                ProtocolVersion = cached.ProtocolVersion,
                Spki = cached.Spki,
                Ttl = ttlSpan,
                Endpoint = new PeerEndpoint { Address = address, Port = cached.Port, InterfaceId = _interfaceId },
            };

            if (cached.Ipv4 is not null) EmitSignal(BuildSeen(cached.Ipv4));
            if (cached.Ipv6 is not null) EmitSignal(BuildSeen(cached.Ipv6));
        }

        void EmitSignal(DiscoverySignal signal)
        {
            try
            {
                _onSignal(signal);
            }
            catch (Exception ex)
            {
                // Never let managed consumer code unwind through a Win32
                // DNS callback. Preserve the exception and continue browsing.
                DiagnosticLog.Current.Error(
                    "discovery.signal-handler-failed",
                    $"kind={signal.GetType().Name} peer={signal.PeerIdHint} interface={signal.InterfaceId}",
                    ex);
            }
        }

        public void Dispose()
        {
            List<PendingResolve> pendingToCancel;

            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                pendingToCancel = _pendingResolves.Values.ToList();
                _pendingResolves.Clear();
            }

            // Outside _gate — same deadlock-avoidance reasoning as HandleGoodbye.
            foreach (var pending in pendingToCancel) pending.Dispose();

            DnsServiceBrowseCancel(ref _cancel);
            // Deliberately not freeing _queryNameHandle/_selfHandle here —
            // see the class remarks. OnBrowseCallback frees them, best
            // effort, on the first invocation it observes after _disposed
            // is set (which is now, above).
        }
    }

    /// <summary>One outstanding DnsServiceResolve call for a single instance
    /// name. Owns the pinned query-name string and the pQueryContext GCHandle
    /// for the lifetime of the native request. The GCHandle's target is this
    /// object itself (see the constructor), so a completion callback can
    /// always recover the InstanceName/Ttl straight from contextPtr even if
    /// this instance has already been removed from ServiceBrowse's
    /// _pendingResolves dictionary by a racing cancellation.
    ///
    /// <b>This is a subscription, not a one-shot call.</b> windns.h documents
    /// DnsServiceResolve's callback as "invoked for each result" (plural) —
    /// so a single PendingResolve can legitimately receive many callback
    /// invocations over its life, not just one. It stays registered in
    /// ServiceBrowse's _pendingResolves for as long as the instance is
    /// visible, and is only ever removed when HandleGoodbye or
    /// ServiceBrowse.Dispose decides it's no longer wanted.
    ///
    /// <b>Handle ownership.</b> <see cref="Dispose"/> (cancellation) never
    /// frees <see cref="_queryNameHandle"/> or the context handle — only
    /// <see cref="FreeHandles"/> does, called from ServiceBrowse.OnResolveComplete
    /// once it observes (via <see cref="WasCancelled"/>) that cancellation
    /// was already requested. Two documented/undocumented Windows behaviors
    /// make any other split unsafe:
    /// 1. Because the callback can fire more than once, freeing on the
    ///    *first* invocation (this class's original implementation) risks a
    ///    second, still-legitimately-in-flight invocation dereferencing an
    ///    already-freed contextPtr.
    /// 2. DnsServiceResolveCancel's synchronization with an in-flight
    ///    completion isn't precisely documented either — it may block until
    ///    a racing completion finishes running (risking a deadlock if
    ///    Dispose() is holding a lock the completion also needs — see
    ///    ServiceBrowse.HandleGoodbye/Dispose, which call this Dispose()
    ///    outside their own lock specifically to avoid that), or it may
    ///    return before a racing completion has been fully delivered.
    /// Making the completion callback the sole owner of the free, gated on
    /// "cancellation was already requested", avoids both failure modes.
    /// FreeHandles' own Interlocked guard makes it safe even if further
    /// post-cancellation invocations still arrive afterward. The residual
    /// tradeoff: if cancellation succeeds and the OS genuinely never
    /// delivers another callback at all afterward (undocumented either
    /// way), this leaks one pinned string and one GCHandle target for the
    /// process's remaining lifetime — bounded by the number of
    /// goodbyes/teardowns, and preferred over a use-after-free.</summary>
    sealed class PendingResolve
    {
        public string InstanceName { get; }
        public uint Ttl { get; }

        /// <summary>True once cancellation has been requested — the signal
        /// ServiceBrowse.OnResolveComplete uses to decide it's finally safe
        /// to free this request's handles.</summary>
        public bool WasCancelled => Volatile.Read(ref _cancelIssued) != 0;

        readonly GCHandle _queryNameHandle;
        readonly GCHandle _contextHandle;
        DNS_SERVICE_RESOLVE_REQUEST _request;
        DNS_SERVICE_CANCEL _cancel;
        int _cancelIssued; // Interlocked-guarded 0/1
        int _handlesFreed; // Interlocked-guarded 0/1

        public PendingResolve(string instanceName, uint ttl, int interfaceIndex, DNS_SERVICE_RESOLVE_COMPLETE callback)
        {
            InstanceName = instanceName;
            Ttl = ttl;

            _queryNameHandle = GCHandle.Alloc(instanceName, GCHandleType.Pinned);
            _contextHandle = GCHandle.Alloc(this);

            _request = new DNS_SERVICE_RESOLVE_REQUEST
            {
                Version = 1,
                InterfaceIndex = (uint)interfaceIndex,
                QueryName = _queryNameHandle.AddrOfPinnedObject(),
                pResolveCompletionCallback = Marshal.GetFunctionPointerForDelegate(callback),
                pQueryContext = GCHandle.ToIntPtr(_contextHandle),
            };

            _cancel = default;
            var status = DnsServiceResolve(ref _request, ref _cancel);
            if (status != DnsQueryResultsFalse && status != DnsRequestPending)
            {
                // The request was never actually queued, so no completion
                // callback is ever coming for it — safe, and necessary, to
                // free our handles right here rather than waiting for one.
                FreeHandles();
                throw new InvalidOperationException($"DnsServiceResolve failed with status {status}.");
            }
        }

        /// <summary>Requests cancellation of the outstanding resolve. Does
        /// NOT free any handles — see the class remarks. Must never be
        /// called while holding a lock the completion callback also needs.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _cancelIssued, 1) != 0) return; // already requested
            DnsServiceResolveCancel(ref _cancel);
        }

        /// <summary>Called exactly once, by ServiceBrowse.OnResolveComplete —
        /// the only point at which it's guaranteed safe to free the handles
        /// this request owns. The Interlocked guard is defense in depth: a
        /// single native completion firing twice for the same request would
        /// itself be an OS-level contract violation, but the cost of
        /// guarding against it is one CompareExchange.</summary>
        public void FreeHandles()
        {
            if (Interlocked.Exchange(ref _handlesFreed, 1) != 0) return;
            if (_queryNameHandle.IsAllocated) _queryNameHandle.Free();
            if (_contextHandle.IsAllocated) _contextHandle.Free();
        }
    }

    readonly record struct CachedInstance(PeerIdHint PeerIdHint, int ProtocolVersion, SpkiPin? Spki, IPAddress? Ipv4, IPAddress? Ipv6, int Port);

    /// <summary>Reads the PTR add/remove notifications off a DNS_RECORD
    /// linked list delivered by a browse callback, and separately reads a
    /// resolved DNS_SERVICE_INSTANCE delivered by a resolve completion.
    /// These are two different native shapes: browse only ever hands back
    /// PTR records (see <see cref="ServiceBrowse"/>'s remarks), while resolve
    /// hands back one fully-populated instance struct — not another
    /// DNS_RECORD list — per windns.h.</summary>
    static class DnsRecordParser
    {
        /// <summary>A TTL of 0 on a PTR record signals removal (goodbye), per
        /// RFC 6762 §10.1.</summary>
        public static IEnumerable<(string InstanceName, uint Ttl)> ParsePtrEvents(IntPtr firstRecord)
        {
            var current = firstRecord;
            while (current != IntPtr.Zero)
            {
                var wType = (ushort)Marshal.ReadInt16(current, 16);
                var ttl = (uint)Marshal.ReadInt32(current, 24);

                if (wType == DnsTypePtr)
                {
                    var dataPtr = IntPtr.Add(current, DnsRecordHeaderSize);
                    var namePtr = Marshal.ReadIntPtr(dataPtr, 0);
                    var instanceName = namePtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(namePtr);
                    if (!string.IsNullOrEmpty(instanceName)) yield return (instanceName, ttl);
                }

                current = Marshal.ReadIntPtr(current, 0); // pNext
            }
        }

        /// <summary>Parses the DNS_SERVICE_INSTANCE handed back by a resolve
        /// completion. Unlike the raw DNS_RECORD TXT format (semicolon-free
        /// "key=value" strings), a resolved instance already carries TXT data
        /// as parallel keys/values arrays, so no further splitting is
        /// needed. Returns null for an instance missing the required
        /// peer-ID-hint TXT entry — never guess an identity.
        ///
        /// <paramref name="interfaceIndex"/> is the browse's own bound
        /// interface index (every resolve request is issued scoped to it —
        /// see <see cref="PendingResolve"/>), used as the IPv6 zone/scope ID
        /// for any link-local address that comes back. DNS_SERVICE_INSTANCE
        /// has no TTL or scope field of its own; docs/research/local-network-
        /// transport.md requires retaining scope for link-local IPv6 on a
        /// multi-homed PC, and an fe80:: address with ScopeId 0 is not
        /// actually connectable.</summary>
        public static CachedInstance? ParseResolvedInstance(IntPtr pInstance, int interfaceIndex)
        {
            var instance = Marshal.PtrToStructure<DNS_SERVICE_INSTANCE>(pInstance);

            IPAddress? ipv4 = null, ipv6 = null;
            if (instance.ip4Address != IntPtr.Zero)
            {
                var bytes = new byte[4];
                Marshal.Copy(instance.ip4Address, bytes, 0, 4);
                ipv4 = new IPAddress(bytes);
            }
            if (instance.ip6Address != IntPtr.Zero)
            {
                var bytes = new byte[16];
                Marshal.Copy(instance.ip6Address, bytes, 0, 16);
                var candidate = new IPAddress(bytes);
                ipv6 = candidate.IsIPv6LinkLocal ? new IPAddress(bytes, interfaceIndex) : candidate;
            }

            string? hintValue = null;
            SpkiPin? spki = null;
            var protocolVersion = 0;
            for (var i = 0; i < instance.dwPropertyCount; i++)
            {
                var keyPtr = Marshal.ReadIntPtr(instance.keys, i * IntPtr.Size);
                var valuePtr = Marshal.ReadIntPtr(instance.values, i * IntPtr.Size);
                var key = keyPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(keyPtr);
                var value = valuePtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(valuePtr);
                if (key is null) continue;

                if (key == DiscoveryProtocol.TxtKeyPeerIdHint) hintValue = value;
                else if (key == DiscoveryProtocol.TxtKeySpki && value is not null)
                {
                    try { spki = new SpkiPin(Convert.FromHexString(value)); }
                    catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
                }
                else if (key == DiscoveryProtocol.TxtKeyVersion && value is not null && int.TryParse(value, out var v)) protocolVersion = v;
            }

            if (string.IsNullOrWhiteSpace(hintValue)) return null;

            return new CachedInstance(new PeerIdHint(hintValue), protocolVersion, spki, ipv4, ipv6, instance.wPort);
        }
    }

    /// <summary>Owns the native allocations backing one DNS_SERVICE_INSTANCE
    /// (strings, address buffers, TXT key/value arrays) for the lifetime of a
    /// registration.</summary>
    sealed class NativeServiceInstance : IDisposable
    {
        readonly List<IntPtr> _allocations = new();
        bool _disposed;

        public IntPtr Pointer { get; }

        public NativeServiceInstance(string instanceName, string hostName, IPAddress? ipv4, IPAddress? ipv6, ushort port, Dictionary<string, string> txt)
        {
            var instance = new DNS_SERVICE_INSTANCE
            {
                pszInstanceName = Alloc(instanceName),
                pszHostName = Alloc(hostName),
                ip4Address = ipv4 is null ? IntPtr.Zero : AllocIp4(ipv4),
                ip6Address = ipv6 is null ? IntPtr.Zero : AllocIp6(ipv6),
                wPort = port,
                wPriority = 0,
                wWeight = 0,
                dwPropertyCount = (uint)txt.Count,
                keys = AllocStringArray(txt.Keys),
                values = AllocStringArray(txt.Values),
                dwInterfaceIndex = 0,
            };

            var size = Marshal.SizeOf<DNS_SERVICE_INSTANCE>();
            Pointer = Marshal.AllocHGlobal(size);
            _allocations.Add(Pointer);
            Marshal.StructureToPtr(instance, Pointer, false);
        }

        IntPtr Alloc(string value)
        {
            var ptr = Marshal.StringToHGlobalUni(value);
            _allocations.Add(ptr);
            return ptr;
        }

        IntPtr AllocIp4(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            var ptr = Marshal.AllocHGlobal(4);
            Marshal.Copy(bytes, 0, ptr, 4);
            _allocations.Add(ptr);
            return ptr;
        }

        IntPtr AllocIp6(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            var ptr = Marshal.AllocHGlobal(16);
            Marshal.Copy(bytes, 0, ptr, 16);
            _allocations.Add(ptr);
            return ptr;
        }

        IntPtr AllocStringArray(IEnumerable<string> values)
        {
            var list = values.ToList();
            if (list.Count == 0) return IntPtr.Zero;

            var arrayPtr = Marshal.AllocHGlobal(IntPtr.Size * list.Count);
            _allocations.Add(arrayPtr);
            for (var i = 0; i < list.Count; i++)
            {
                var strPtr = Alloc(list[i]);
                Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, strPtr);
            }
            return arrayPtr;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var ptr in _allocations) Marshal.FreeHGlobal(ptr);
        }
    }

    // ---- Native declarations (dnsapi.dll) ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DNS_SERVICE_INSTANCE
    {
        public IntPtr pszInstanceName;
        public IntPtr pszHostName;
        public IntPtr ip4Address;
        public IntPtr ip6Address;
        public ushort wPort;
        public ushort wPriority;
        public ushort wWeight;
        public uint dwPropertyCount;
        public IntPtr keys;
        public IntPtr values;
        public uint dwInterfaceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DNS_SERVICE_CANCEL
    {
        public IntPtr reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void DNS_SERVICE_REGISTER_COMPLETE(uint status, IntPtr pQueryContext, IntPtr pInstance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void DNS_SERVICE_BROWSE_CALLBACK(uint status, IntPtr pQueryContext, IntPtr pDnsRecord);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void DNS_SERVICE_RESOLVE_COMPLETE(uint status, IntPtr pQueryContext, IntPtr pInstance);

    [StructLayout(LayoutKind.Sequential)]
    struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        [MarshalAs(UnmanagedType.Bool)] public bool unicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DNS_SERVICE_BROWSE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr pBrowseCallback;
        public IntPtr pQueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DNS_SERVICE_RESOLVE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr pResolveCompletionCallback;
        public IntPtr pQueryContext;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint DnsServiceRegister(ref DNS_SERVICE_REGISTER_REQUEST pRequest, IntPtr pCancel);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint DnsServiceDeRegister(ref DNS_SERVICE_REGISTER_REQUEST pRequest, IntPtr pCancel);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint DnsServiceBrowse(ref DNS_SERVICE_BROWSE_REQUEST pRequest, ref DNS_SERVICE_CANCEL pCancel);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint DnsServiceBrowseCancel(ref DNS_SERVICE_CANCEL pCancelHandle);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint DnsServiceResolve(ref DNS_SERVICE_RESOLVE_REQUEST pRequest, ref DNS_SERVICE_CANCEL pCancel);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint DnsServiceResolveCancel(ref DNS_SERVICE_CANCEL pCancelHandle);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern void DnsServiceFreeInstance(IntPtr pInstance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    static extern void DnsRecordListFree(IntPtr pRecordList, int freeType);
}
