using System.Diagnostics;
using System.Net.NetworkInformation;
using Intercom.Discovery;
using Intercom.Identity;

return args.Length > 0 && args[0] == "peer"
    ? await RunPeerAsync(args)
    : await RunOrchestratorAsync();

static async Task<int> RunOrchestratorAsync()
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot resolve the integration harness executable.");
    var firstId = Guid.NewGuid().ToString("N");
    var secondId = Guid.NewGuid().ToString("N");

    using var first = StartPeer(executable, firstId, secondId);
    using var second = StartPeer(executable, secondId, firstId);
    var outputs = await Task.WhenAll(CaptureAsync(first), CaptureAsync(second));

    foreach (var output in outputs) Console.Write(output);
    if (outputs.All(output => output.Contains("DISCOVERED", StringComparison.Ordinal)))
    {
        Console.WriteLine("PASS: both processes discovered the other peer through Windows DNS-SD.");
        return 0;
    }

    Console.Error.WriteLine("FAIL: at least one process did not discover the other through Windows DNS-SD.");
    return 1;
}

static Process StartPeer(string executable, string ownId, string expectedId)
{
    var startInfo = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add("peer");
    startInfo.ArgumentList.Add(ownId);
    startInfo.ArgumentList.Add(expectedId);
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start discovery peer process.");
}

static async Task<string> CaptureAsync(Process process)
{
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return await stdout + await stderr;
}

static async Task<int> RunPeerAsync(string[] args)
{
    var ownId = new PeerIdHint(args[1]);
    var expectedId = new PeerIdHint(args[2]);
    var iface = new SystemNetworkInterfaceSnapshotProvider()
        .GetCurrentInterfaces()
        .FirstOrDefault(candidate =>
            candidate.OperationalStatus == OperationalStatus.Up &&
            candidate.SupportsMulticast &&
            candidate.UnicastAddresses.Count > 0 &&
            candidate.Type is not NetworkInterfaceType.Loopback and
                not NetworkInterfaceType.Tunnel);

    if (iface is null)
    {
        Console.Error.WriteLine("NO_INTERFACE: no active multicast-capable LAN interface.");
        return 2;
    }

    Console.WriteLine($"START {ownId} interface={iface.Name} ipv4Index={iface.Ipv4InterfaceIndex} ipv6Index={iface.Ipv6InterfaceIndex}");
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var discovery = new Win32DnsServiceDiscovery();
    var browse = discovery.Browse(iface, signal =>
    {
        if (signal is DiscoverySignal.Seen && signal.PeerIdHint == expectedId)
            observed.TrySetResult();
    });
    var registration = discovery.Register(iface, new ServiceAdvertisement
    {
        PeerIdHint = ownId,
        ProtocolVersion = DiscoveryProtocol.CurrentVersion,
        Spki = new SpkiPin(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ownId.Value))),
        Port = 43136,
    });

    var exitCode = 0;
    try
    {
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"DISCOVERED {expectedId}");
        // Keep advertising briefly after success. Without this barrier the
        // first peer to discover its sibling can exit and send a goodbye
        // before the sibling's outstanding resolve completes, making the
        // two-process assertion race its own teardown.
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($"TIMEOUT expected={expectedId}");
        exitCode = 1;
    }
    finally
    {
        registration.Dispose();
        browse.Dispose();

        // Win32 DNS-SD deregistration completes asynchronously. Keep the
        // process alive long enough to publish the goodbye packet, otherwise
        // test GUIDs remain visible until their full TTL expires.
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    return exitCode;
}
