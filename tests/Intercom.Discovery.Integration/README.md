# Windows DNS-SD integration harness

This executable launches two independent processes against the real
`Win32DnsServiceDiscovery` implementation. Each process advertises a unique
peer ID on the first active multicast-capable LAN interface and the command
fails unless both processes discover the other.

Run it from the repository root on Windows:

```powershell
dotnet run --project tests\Intercom.Discovery.Integration\Intercom.Discovery.Integration.csproj
```

This covers the native `DnsServiceRegister`/`DnsServiceBrowse`/
`DnsServiceResolve` path without fakes. It does not replace the final
two-computer packaged-app check because one host cannot reproduce firewall,
network-profile, access-point isolation, or cross-device multicast behavior.
