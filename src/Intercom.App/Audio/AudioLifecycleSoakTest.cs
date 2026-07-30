using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Intercom.Audio;
using Intercom.Diagnostics;

namespace Intercom.App.Audio;

public sealed record AudioLifecycleSnapshot(
    long PrivateMemory,
    long ManagedMemory,
    int Handles,
    int UserHandles,
    int GdiHandles,
    int Threads);

public sealed record AudioLifecycleProgress(
    int CompletedCycles,
    AudioLifecycleSnapshot Resources,
    string InputDevice,
    string OutputDevice);

public sealed record AudioDeviceTransition(int FirstCycle, string InputDevice, string OutputDevice);

public sealed record AudioLifecycleSoakResult(
    int CompletedCycles,
    AudioLifecycleSnapshot Before,
    AudioLifecycleSnapshot After,
    IReadOnlyList<AudioDeviceTransition> DeviceTransitions)
{
    public string Summary =>
        $"Completed {CompletedCycles} cycles. " +
        $"Private memory {FormatBytes(Before.PrivateMemory)} → {FormatBytes(After.PrivateMemory)} " +
        $"({FormatSignedBytes(After.PrivateMemory - Before.PrivateMemory)}); " +
        $"managed memory {FormatBytes(Before.ManagedMemory)} → {FormatBytes(After.ManagedMemory)} " +
        $"({FormatSignedBytes(After.ManagedMemory - Before.ManagedMemory)}); " +
        $"handles {Before.Handles} → {After.Handles} " +
        $"(USER {Before.UserHandles} → {After.UserHandles}, GDI {Before.GdiHandles} → {After.GdiHandles}); " +
        $"threads {Before.Threads} → {After.Threads}. " +
        $"Devices: {FormatDeviceTransitions()}. " +
        "Every disposed UDP port was rebound successfully.";

    string FormatDeviceTransitions() => string.Join("; ", DeviceTransitions.Select(transition =>
        $"cycle {transition.FirstCycle}: input=[{transition.InputDevice}], output=[{transition.OutputDevice}]"));

    static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
    static string FormatSignedBytes(long bytes) => $"{(bytes >= 0 ? "+" : "-")}{FormatBytes(Math.Abs(bytes))}";
}

public static partial class AudioLifecycleSoakTest
{
    public const int CycleCount = 100;
    const int CheckpointInterval = 10;
    const uint GrUserObjects = 1;
    const uint GrGdiObjects = 0;

    public static async Task<AudioLifecycleSoakResult> RunAsync(
        IProgress<AudioLifecycleProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int cycleCount = CycleCount)
    {
        CollectGarbage();
        var before = TakeSnapshot();
        var transitions = new List<AudioDeviceTransition>();

        for (var cycle = 1; cycle <= cycleCount; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await RunCycleAsync(cancellationToken);
            if (transitions.Count == 0 ||
                transitions[^1].InputDevice != devices.InputDevice ||
                transitions[^1].OutputDevice != devices.OutputDevice)
                transitions.Add(new AudioDeviceTransition(cycle, devices.InputDevice, devices.OutputDevice));

            if (cycle % CheckpointInterval == 0 || cycle == 1)
            {
                var checkpoint = TakeSnapshot();
                var update = new AudioLifecycleProgress(cycle, checkpoint, devices.InputDevice, devices.OutputDevice);
                progress?.Report(update);
                DiagnosticLog.Current.Info("audio.lifecycle-soak-checkpoint", FormatCheckpoint(update));
            }
        }

        CollectGarbage();
        var after = TakeSnapshot();
        return new AudioLifecycleSoakResult(cycleCount, before, after, transitions);
    }

    static async Task<(string InputDevice, string OutputDevice)> RunCycleAsync(CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var receiver = new UdpAudioReceiver(0, key, 41);
        var port = receiver.LocalPort;
        var sender = new UdpAudioSender(new IPEndPoint(IPAddress.IPv6Loopback, port), key, 41);
        var streamId = Guid.NewGuid();
        AudioPipelineDiagnostics diagnostics;
        await using (var session = new AudioPipelineSession(
            Guid.NewGuid(),
            streamId,
            streamId,
            new AudioGraphDevice(),
            sender,
            receiver))
        {
            await session.StartAsync();
            diagnostics = session.Diagnostics;
            session.StartTransmitting();
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            await session.StopAsync();
        }

        using var rebound = new UdpClient(AddressFamily.InterNetworkV6);
        rebound.Client.DualMode = true;
        rebound.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        return (diagnostics.InputDeviceName, diagnostics.OutputDeviceName);
    }

    static AudioLifecycleSnapshot TakeSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new AudioLifecycleSnapshot(
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            process.HandleCount,
            checked((int)GetGuiResources(process.Handle, GrUserObjects)),
            checked((int)GetGuiResources(process.Handle, GrGdiObjects)),
            process.Threads.Count);
    }

    static string FormatCheckpoint(AudioLifecycleProgress progress) =>
        $"cycle={progress.CompletedCycles} " +
        $"privateMb={progress.Resources.PrivateMemory / 1024d / 1024d:0.0} " +
        $"managedMb={progress.Resources.ManagedMemory / 1024d / 1024d:0.0} " +
        $"handles={progress.Resources.Handles} userHandles={progress.Resources.UserHandles} " +
        $"gdiHandles={progress.Resources.GdiHandles} threads={progress.Resources.Threads} " +
        $"input=\"{progress.InputDevice}\" output=\"{progress.OutputDevice}\"";

    static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint process, uint flags);

}
