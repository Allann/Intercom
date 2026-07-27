using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Intercom.Diagnostics;
using Intercom.Discovery;
using Intercom.Identity;
using Intercom.Lifecycle;
using Intercom.App.Startup;
using Intercom.App.Tray;

namespace Intercom.App;

public partial class App : Application
{
    static readonly Guid TrayIconGuid = new("6a1f2e2e-6b7a-4a6a-9a1e-8f1c1f6a2b1a");

    // TODO(#21 reliable control channel): replace with the actual bound port
    // of the mutual-TLS TCP listener once it exists. Discovery still needs a
    // port to put in the SRV record even though nothing is listening on it
    // yet, so peers are visible for the two-PC discovery pass ahead of #21.
    const int PlaceholderControlChannelPort = 47811;

    MainWindow? _mainWindow;

    readonly AppLifecycle _lifecycle;
    DiscoveryService? _discoveryService;

    Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcherQueue;

    public App()
    {
        InitializeComponent();

        _lifecycle = new AppLifecycle(
            new CrashMarker(),
            new IdentityStore(),
            new StartupTaskService(),
            createWindow: () => _mainWindow = new MainWindow(),
            createTrayPump: hwnd => new TrayMessagePump(hwnd),
            createTrayIcon: hwnd => new TrayIcon(hwnd, TrayIconGuid));
        _lifecycle.QuitRequested += OnQuitRequested;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var launchedViaStartupTask = Program.InitialActivationArguments.Kind == ExtendedActivationKind.StartupTask;
        var outcome = _lifecycle.Start(launchedViaStartupTask);

        if (outcome.IdentityWasRegenerated)
        {
            // TODO(#22 pairing ceremony): surface this via the shell UI — every
            // previously approved peer is now unknown and needs re-pairing.
            System.Diagnostics.Debug.WriteLine("Local identity was unreadable and has been regenerated.");
        }
        else if (outcome.RegistryWasReset)
        {
            // TODO(#22 pairing ceremony): surface this too — the identity is
            // still valid, but the approved-peer list itself was unreadable
            // and every peer now needs re-pairing.
            System.Diagnostics.Debug.WriteLine("Approved-peer registry was unreadable and has been reset.");
        }
        if (outcome.PendingPairingsWereReset)
        {
            System.Diagnostics.Debug.WriteLine("Pending-pairing state was unreadable and has been reset.");
        }

        StartDiscovery();

        Program.RedirectedActivationReceived += OnRedirectedActivation;
    }

    /// <summary>Issue #20: makes discovered-but-unapproved local peers
    /// visible. Deliberately nothing more than that — no trust, no
    /// connection (#21/#22).</summary>
    void StartDiscovery()
    {
        _discoveryService = new DiscoveryService(
            new Win32DnsServiceDiscovery(),
            new SystemNetworkInterfaceSnapshotProvider(),
            new SystemNetworkChangeNotifier(),
            PeerIdHint.FromPeerId(_lifecycle.Identity.PeerId),
            PlaceholderControlChannelPort);

        _discoveryService.VisiblePeersChanged += OnVisiblePeersChanged;
        _discoveryService.Start();
    }

    void OnVisiblePeersChanged()
    {
        // Native DNS-SD callbacks arrive on arbitrary threads; UI updates
        // must be marshaled back onto the UI dispatcher.
        var peers = _discoveryService?.VisiblePeers;
        if (peers is null) return;
        _uiDispatcherQueue?.TryEnqueue(() => _mainWindow?.UpdateDiscoveredPeers(peers));
    }

    void OnRedirectedActivation(AppActivationArguments args)
    {
        _uiDispatcherQueue?.TryEnqueue(_lifecycle.ShowWindow);
    }

    void OnQuitRequested()
    {
        Program.RedirectedActivationReceived -= OnRedirectedActivation;
        if (_discoveryService is not null)
        {
            _discoveryService.VisiblePeersChanged -= OnVisiblePeersChanged;
            _discoveryService.Dispose();
        }
        _lifecycle.Quit();
        Exit();
    }
}
