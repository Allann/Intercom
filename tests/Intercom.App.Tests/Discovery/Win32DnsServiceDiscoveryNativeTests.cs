using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Intercom.Discovery;
using Intercom.Identity;
using Xunit;

namespace Intercom.App.Tests.Discovery;

public sealed class Win32DnsServiceDiscoveryNativeTests
{
    [Fact]
    public void RegistrationFreesEachCallbackInstanceOnceAndReleaseIsIdempotent()
    {
        var native = new FakeNative();
        var sut = new Win32DnsServiceDiscovery(native);
        var registration = sut.Register(Interface(), new ServiceAdvertisement
        {
            PeerIdHint = new PeerIdHint("peer-a"), ProtocolVersion = 1, Port = 4242,
        });

        var first = new IntPtr(101);
        native.CompleteRegister(first);
        registration.Dispose();
        registration.Dispose();
        var second = new IntPtr(102);
        native.CompleteRegister(second);

        Assert.Equal(1, native.DeregisterCalls);
        Assert.Equal([first, second], native.FreedInstances);
    }

    [Fact]
    public void BrowseAddsRefreshesAndWithdrawsResolvedService()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);

        native.PublishPtr("instance.local", 30);
        Assert.Equal(1, native.ResolveCalls);
        native.CompleteResolve("peer-a", 4242);
        native.PublishPtr("instance.local", 60);
        native.PublishPtr("instance.local", 0);

        Assert.Collection(signals,
            first => Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<DiscoverySignal.Seen>(first).Ttl),
            refresh => Assert.Equal(TimeSpan.FromSeconds(60), Assert.IsType<DiscoverySignal.Seen>(refresh).Ttl),
            withdrawn => Assert.Equal("peer-a", Assert.IsType<DiscoverySignal.Withdrawn>(withdrawn).PeerIdHint.Value));
        Assert.Equal(1, native.CancelResolveCalls);
        Assert.Equal(3, native.FreedRecordLists.Count);
        Assert.Single(native.FreedInstances);
    }

    [Fact]
    public void ActiveResolveSubscriptionAcceptsMoreThanOneCompletionCallback()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        native.CompleteResolve("peer-a", 4242);
        native.CompleteResolve("peer-a", 4243);

        Assert.Equal([4242, 4243], signals.Select(signal => Assert.IsType<DiscoverySignal.Seen>(signal).Endpoint.Port));
    }

    [Fact]
    public void ResolveErrorAndDisposeRaceFreeNativeResultsAndCancelOnce()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        browse.Dispose();
        browse.Dispose();
        native.CompleteResolve("peer-a", 4242);
        native.CompleteBrowseCancellation();

        Assert.Empty(signals);
        Assert.Equal(1, native.CancelBrowseCalls);
        Assert.Equal(1, native.CancelResolveCalls);
        Assert.Single(native.FreedInstances);
    }

    [Fact]
    public void ResolveErrorWithInstanceStillReleasesNativeInstance()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        native.CompleteResolve("peer-a", 4242, status: 5);

        Assert.Empty(signals);
        Assert.Single(native.FreedInstances);
    }

    [Fact]
    public void ResolveCallbackWithMissingContextFreesItsNativeInstanceAndEmitsNothing()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        native.CompleteResolveWithMissingContext("peer-a", 4242);

        Assert.Empty(signals);
        Assert.Single(native.FreedInstances);
    }

    [Fact]
    public void ResolveCallbackWithNullInstanceEmitsNothing()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        native.CompleteResolveWithNullInstance();

        Assert.Empty(signals);
        Assert.Empty(native.FreedInstances);
    }

    [Fact]
    public void LateResolveAfterGoodbyeCannotRestoreTheWithdrawnPeer()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);
        native.CompleteResolve("peer-a", 4242);
        native.PublishPtr("instance.local", 0);

        native.CompleteResolve("peer-a", 4242);

        Assert.Equal(2, signals.Count);
        Assert.IsType<DiscoverySignal.Withdrawn>(signals[1]);
    }

    [Fact]
    public void ResolvedIpv4AndIpv6BytesAreCopiedIntoSeparateSeenSignals()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        using var browse = new Win32DnsServiceDiscovery(native).Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        native.CompleteResolve("peer-a", 4242, includeIpv6: true);

        Assert.Contains(signals, signal => Assert.IsType<DiscoverySignal.Seen>(signal).Endpoint.Address.Equals(IPAddress.Parse("192.168.1.20")));
        Assert.Contains(signals, signal => Assert.IsType<DiscoverySignal.Seen>(signal).Endpoint.Address.Equals(IPAddress.Parse("fe80::1%7")));
    }

    [Fact]
    public void GoodbyeBetweenResolveParsingAndCachingPreventsAStaleSeenSignal()
    {
        var native = new FakeNative();
        var signals = new List<DiscoverySignal>();
        var discovery = new Win32DnsServiceDiscovery(native, () => native.PublishPtr("instance.local", 0));
        using var browse = discovery.Browse(Interface(), signals.Add);
        native.PublishPtr("instance.local", 30);

        native.CompleteResolve("peer-a", 4242);

        Assert.Empty(signals);
        Assert.Equal(1, native.CancelResolveCalls);
    }

    static LanInterface Interface() => new()
    {
        Id = "if-a", Name = "Ethernet", Type = NetworkInterfaceType.Ethernet,
        OperationalStatus = OperationalStatus.Up, SupportsMulticast = true,
        UnicastAddresses = [IPAddress.Parse("192.168.1.10")], Ipv4InterfaceIndex = 7,
    };

    sealed class FakeNative : Win32DnsServiceDiscovery.IDnsServiceNativeApi
    {
        Win32DnsServiceDiscovery.DNS_SERVICE_REGISTER_COMPLETE? _register;
        Win32DnsServiceDiscovery.DNS_SERVICE_BROWSE_CALLBACK? _browse;
        Win32DnsServiceDiscovery.DNS_SERVICE_RESOLVE_COMPLETE? _resolve;
        IntPtr _resolveContext;
        readonly Dictionary<IntPtr, List<IntPtr>> _owned = new();

        public int DeregisterCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public int CancelResolveCalls { get; private set; }
        public int CancelBrowseCalls { get; private set; }
        public List<IntPtr> FreedInstances { get; } = [];
        public List<IntPtr> FreedRecordLists { get; } = [];

        public uint Register(ref Win32DnsServiceDiscovery.DNS_SERVICE_REGISTER_REQUEST request)
        {
            _register = Marshal.GetDelegateForFunctionPointer<Win32DnsServiceDiscovery.DNS_SERVICE_REGISTER_COMPLETE>(request.pRegisterCompletionCallback);
            return 9506;
        }

        public uint Deregister(ref Win32DnsServiceDiscovery.DNS_SERVICE_REGISTER_REQUEST request)
        {
            DeregisterCalls++;
            return 9506;
        }

        public uint Browse(ref Win32DnsServiceDiscovery.DNS_SERVICE_BROWSE_REQUEST request, ref Win32DnsServiceDiscovery.DNS_SERVICE_CANCEL cancel)
        {
            _browse = Marshal.GetDelegateForFunctionPointer<Win32DnsServiceDiscovery.DNS_SERVICE_BROWSE_CALLBACK>(request.pBrowseCallback);
            return 9506;
        }

        public uint CancelBrowse(ref Win32DnsServiceDiscovery.DNS_SERVICE_CANCEL cancel)
        {
            CancelBrowseCalls++;
            return 0;
        }

        public uint Resolve(ref Win32DnsServiceDiscovery.DNS_SERVICE_RESOLVE_REQUEST request, ref Win32DnsServiceDiscovery.DNS_SERVICE_CANCEL cancel)
        {
            ResolveCalls++;
            _resolve = Marshal.GetDelegateForFunctionPointer<Win32DnsServiceDiscovery.DNS_SERVICE_RESOLVE_COMPLETE>(request.pResolveCompletionCallback);
            _resolveContext = request.pQueryContext;
            return 9506;
        }

        public uint CancelResolve(ref Win32DnsServiceDiscovery.DNS_SERVICE_CANCEL cancel)
        {
            CancelResolveCalls++;
            return 0;
        }

        public void FreeInstance(IntPtr instance)
        {
            FreedInstances.Add(instance);
            FreeOwned(instance);
        }

        public void FreeRecordList(IntPtr records)
        {
            FreedRecordLists.Add(records);
            FreeOwned(records);
        }

        public void CompleteRegister(IntPtr instance) => _register!(0, IntPtr.Zero, instance);

        public void PublishPtr(string name, uint ttl)
        {
            var record = Own(Marshal.AllocHGlobal(40));
            for (var i = 0; i < 40; i++) Marshal.WriteByte(record, i, 0);
            Marshal.WriteInt16(record, 16, 12);
            Marshal.WriteInt32(record, 24, unchecked((int)ttl));
            var namePointer = Own(record, Marshal.StringToHGlobalUni(name));
            Marshal.WriteIntPtr(record, 32, namePointer);
            _browse!(0, IntPtr.Zero, record);
        }

        public void CompleteResolve(string hint, ushort port, uint status = 0, bool includeIpv6 = false)
        {
            var pointer = CreateInstance(hint, port, includeIpv6);
            _resolve!(status, _resolveContext, pointer);
        }

        public void CompleteResolveWithMissingContext(string hint, ushort port) =>
            _resolve!(0, IntPtr.Zero, CreateInstance(hint, port, false));

        public void CompleteResolveWithNullInstance() => _resolve!(0, _resolveContext, IntPtr.Zero);

        public void CompleteBrowseCancellation() => _browse!(1223, IntPtr.Zero, IntPtr.Zero);

        IntPtr CreateInstance(string hint, ushort port, bool includeIpv6)
        {
            var pointer = Own(Marshal.AllocHGlobal(Marshal.SizeOf<Win32DnsServiceDiscovery.DNS_SERVICE_INSTANCE>()));
            var keyArray = Own(pointer, Marshal.AllocHGlobal(IntPtr.Size));
            var valueArray = Own(pointer, Marshal.AllocHGlobal(IntPtr.Size));
            var ipv4 = Own(pointer, Marshal.AllocHGlobal(4));
            Marshal.Copy(new byte[] { 192, 168, 1, 20 }, 0, ipv4, 4);
            var ipv6 = IntPtr.Zero;
            if (includeIpv6)
            {
                ipv6 = Own(pointer, Marshal.AllocHGlobal(16));
                Marshal.Copy(IPAddress.Parse("fe80::1").GetAddressBytes(), 0, ipv6, 16);
            }
            Marshal.WriteIntPtr(keyArray, Own(pointer, Marshal.StringToHGlobalUni(DiscoveryProtocol.TxtKeyPeerIdHint)));
            Marshal.WriteIntPtr(valueArray, Own(pointer, Marshal.StringToHGlobalUni(hint)));
            Marshal.StructureToPtr(new Win32DnsServiceDiscovery.DNS_SERVICE_INSTANCE
            {
                ip4Address = ipv4, ip6Address = ipv6, wPort = port, dwPropertyCount = 1, keys = keyArray, values = valueArray,
            }, pointer, false);
            return pointer;
        }

        IntPtr Own(IntPtr pointer)
        {
            _owned[pointer] = [pointer];
            return pointer;
        }

        IntPtr Own(IntPtr owner, IntPtr pointer)
        {
            _owned[owner].Add(pointer);
            return pointer;
        }

        void FreeOwned(IntPtr owner)
        {
            if (!_owned.Remove(owner, out var pointers)) return;
            foreach (var pointer in pointers.AsEnumerable().Reverse()) Marshal.FreeHGlobal(pointer);
        }
    }
}
