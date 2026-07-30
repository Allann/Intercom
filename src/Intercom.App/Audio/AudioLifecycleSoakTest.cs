using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Intercom.Audio;

namespace Intercom.App.Audio;

public sealed record AudioLifecycleSoakResult(
    int CompletedCycles,
    long PrivateMemoryBefore,
    long PrivateMemoryAfter,
    int HandlesBefore,
    int HandlesAfter,
    int ThreadsBefore,
    int ThreadsAfter)
{
    public string Summary =>
        $"Completed {CompletedCycles} cycles. " +
        $"Private memory {FormatBytes(PrivateMemoryBefore)} → {FormatBytes(PrivateMemoryAfter)} " +
        $"({FormatSignedBytes(PrivateMemoryAfter - PrivateMemoryBefore)}); " +
        $"handles {HandlesBefore} → {HandlesAfter}; " +
        $"threads {ThreadsBefore} → {ThreadsAfter}. " +
        "Every disposed UDP port was rebound successfully.";

    static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
    static string FormatSignedBytes(long bytes) => $"{(bytes >= 0 ? "+" : "-")}{FormatBytes(Math.Abs(bytes))}";
}

public static class AudioLifecycleSoakTest
{
    public const int CycleCount = 100;

    public static async Task<AudioLifecycleSoakResult> RunAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CollectGarbage();
        var before = TakeSnapshot();

        for (var cycle = 1; cycle <= CycleCount; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunCycleAsync(cancellationToken);
            progress?.Report(cycle);
        }

        CollectGarbage();
        var after = TakeSnapshot();
        return new AudioLifecycleSoakResult(
            CycleCount,
            before.PrivateMemory,
            after.PrivateMemory,
            before.Handles,
            after.Handles,
            before.Threads,
            after.Threads);
    }

    static async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var receiver = new UdpAudioReceiver(0, key, 41);
        var port = receiver.LocalPort;
        var sender = new UdpAudioSender(new IPEndPoint(IPAddress.IPv6Loopback, port), key, 41);
        var streamId = Guid.NewGuid();
        await using (var session = new AudioPipelineSession(
            Guid.NewGuid(),
            streamId,
            streamId,
            new AudioGraphDevice(),
            sender,
            receiver))
        {
            await session.StartAsync();
            session.StartTransmitting();
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            await session.StopAsync();
        }

        using var rebound = new UdpClient(AddressFamily.InterNetworkV6);
        rebound.Client.DualMode = true;
        rebound.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
    }

    static ResourceSnapshot TakeSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ResourceSnapshot(process.PrivateMemorySize64, process.HandleCount, process.Threads.Count);
    }

    static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    sealed record ResourceSnapshot(long PrivateMemory, int Handles, int Threads);
}
