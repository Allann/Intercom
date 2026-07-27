using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Intercom.Identity;
using Intercom.Pairing;

namespace Intercom.App.Pairing;

/// <summary>
/// The Postcard Badges pairing flow (issue #7 prototype, ADR-0002, issue
/// #22): a "card deck" of in-progress pairing ceremonies with a wax-seal
/// six-digit code and stamp-to-approve/tear-up-to-reject actions, plus a
/// Rolodex of already-approved peers for rename/revoke. Opened as a modal
/// dialog over the shell from MainWindow's "+ Add Family Member" action, per
/// docs/mvp-specification.md §7.
///
/// Reads/writes the real <see cref="IdentityStore"/> passed in — the
/// Rolodex, renames, and revocations are genuine, persisted operations, not
/// mock data. The one thing this sandbox cannot demonstrate is a live
/// ceremony against a second physical device; "Simulate a new pairing
/// request" instead runs a completely real ceremony (real nonces, transcript,
/// rejection-sampled code, confirmation state machine, and
/// <see cref="IdentityStore.Approve"/> call) against an in-memory
/// <see cref="LoopbackPairingTransport"/> pair standing in for the second
/// device — see that type's doc comment for exactly what is and isn't real
/// about it.
/// </summary>
public sealed partial class PairingDialog : ContentDialog
{
    static readonly SolidColorBrush Cream = new(Color.FromArgb(255, 0xFB, 0xF4, 0xE4));
    static readonly SolidColorBrush Ink = new(Color.FromArgb(255, 0x2B, 0x1D, 0x14));
    static readonly SolidColorBrush Mustard = new(Color.FromArgb(255, 0xE8, 0xA3, 0x3D));
    static readonly SolidColorBrush Green = new(Color.FromArgb(255, 0x5C, 0x8A, 0x52));
    static readonly SolidColorBrush Red = new(Color.FromArgb(255, 0xC1, 0x44, 0x3A));
    static readonly SolidColorBrush Teal = new(Color.FromArgb(255, 0x2F, 0x6F, 0x6B));

    readonly IdentityStore _identityStore;
    readonly PairingSessionManager _sessionManager;
    readonly DispatcherQueue _dispatcherQueue;
    readonly DispatcherQueueTimer _tickTimer;

    /// <summary>One entry per postcard currently on the deck, tracking both
    /// the "local device" and simulated "peer device" halves of a demo
    /// ceremony so the demo buttons can drive either side.</summary>
    readonly List<PostcardEntry> _postcards = [];

    /// <summary>Live loopback transports for peers approved via a demo
    /// ceremony during this dialog session — lets "Return to Sender" best-
    /// effort-send a Forgotten notice for those specific peers, mirroring
    /// what a real live connection would allow (ADR-0002: best-effort only
    /// if the connection happens to be live; a peer approved in an earlier
    /// app session has no such live connection here, and correctly gets no
    /// notice).</summary>
    readonly Dictionary<Guid, IPairingTransport> _liveTransportsByPeerId = [];

    public PairingDialog(IdentityStore identityStore)
    {
        InitializeComponent();

        _identityStore = identityStore;
        _sessionManager = new PairingSessionManager(identityStore);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        RenderRolodex();

        _tickTimer = _dispatcherQueue.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.Tick += (_, _) => _sessionManager.Tick(DateTimeOffset.UtcNow);
        _tickTimer.Start();

        Closed += (_, _) =>
        {
            _tickTimer.Stop();
            CleanUpDemoPeerStores();
        };
    }

    void OnSimulateRequestClick(object sender, RoutedEventArgs e) => StartDemoCeremony();

    void StartDemoCeremony()
    {
        var (localTransport, peerTransport) = LoopbackPairingTransport.CreatePair();

        // A throwaway identity store for the simulated "other device" — its
        // own approval bookkeeping is real (it really calls Approve), but it
        // is never surfaced in this device's Rolodex, the same way a real
        // second device's approved-peer list is never visible on this one.
        var peerStoreDir = Path.Combine(Path.GetTempPath(), "IntercomPairingDemo_" + Guid.NewGuid());
        Directory.CreateDirectory(peerStoreDir);
        var peerStore = new IdentityStore(peerStoreDir);
        peerStore.LoadOrCreate();

        var now = DateTimeOffset.UtcNow;
        var localSpki = _identityStore.Identity.SpkiSha256;
        var localPeerId = _identityStore.Identity.PeerId;
        var peerSpki = peerStore.Identity.SpkiSha256;
        var peerPeerId = peerStore.Identity.PeerId;
        var peerCertificate = peerStore.Identity.Certificate.RawData;
        var localCertificate = _identityStore.Identity.Certificate.RawData;

        var localSession = _sessionManager.TryStartSession(
            source: "demo", localTransport, localSpki, localPeerId, peerSpki, peerPeerId, peerCertificate, now);
        if (localSession is null)
        {
            // Rate-limited or already outstanding for this (freshly
            // generated) peer id — vanishingly unlikely for a fresh demo
            // peer, but fail visibly rather than silently doing nothing.
            NoPostcardsNotice.Text = "Couldn't start a new pairing request right now (rate limit or duplicate). Try again shortly.";
            Directory.Delete(peerStoreDir, recursive: true);
            return;
        }

        // The simulated peer's own coordinator is deliberately NOT routed
        // through a PairingSessionManager/rate limiter of its own — it
        // exists only to let the demo's "peer" buttons produce real wire
        // frames, not to re-exercise rate limiting a second time.
        var peerSession = new PairingCeremonyCoordinator(
            peerTransport, peerStore, peerSpki, peerPeerId, localSpki, localPeerId, localCertificate, now);

        var entry = new PostcardEntry(localSession, peerSession, peerStoreDir);
        _postcards.Add(entry);

        var card = BuildPostcardVisual(entry);
        entry.CardBorder = card;
        PostcardDeckPanel.Children.Add(card);
        NoPostcardsNotice.Visibility = Visibility.Collapsed;

        localSession.CodeReady += code => _dispatcherQueue.TryEnqueue(() => UpdatePostcardCode(entry, code));
        localSession.Approved += peer => _dispatcherQueue.TryEnqueue(() =>
        {
            _liveTransportsByPeerId[peer.PeerId] = localTransport;
            OnLocalOutcome(entry, "Approved!", Green);
            RenderRolodex();
        });
        localSession.Rejected += () => _dispatcherQueue.TryEnqueue(() => OnLocalOutcome(entry, "Torn up.", Red));
        localSession.Expired += () => _dispatcherQueue.TryEnqueue(() => OnLocalOutcome(entry, "Expired (2 min).", Ink));

        _ = RunDemoStartAsync(localSession, peerSession);
    }

    async Task RunDemoStartAsync(PairingCeremonyCoordinator localSession, PairingCeremonyCoordinator peerSession)
    {
        // Order doesn't matter for correctness (BuildTranscript/ComputeCode
        // are symmetric — see PairingTranscriptTests), but starting both
        // sides is what lets each compute the shared code.
        await localSession.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await peerSession.StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    Border BuildPostcardVisual(PostcardEntry entry)
    {
        var stack = new StackPanel { Spacing = 6, Width = 210 };

        stack.Children.Add(new TextBlock { Text = "📮", FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = "Simulated peer",
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var sealText = new TextBlock
        {
            Text = "· · · · · ·",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Teal,
        };
        entry.SealText = sealText;
        stack.Children.Add(sealText);

        var statusText = new TextBlock
        {
            Text = "Waiting for both nonces...",
            FontSize = 11,
            Foreground = Ink,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        entry.StatusTextBlock = statusText;
        stack.Children.Add(statusText);

        var yourRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var stampButton = new Button { Content = "Stamp Approved", Background = Green, Foreground = Cream };
        stampButton.Click += (_, _) => _ = OnStampClickAsync(entry);
        var tearButton = new Button { Content = "Tear Up", Background = Red, Foreground = Cream };
        tearButton.Click += (_, _) => _ = OnTearClickAsync(entry);
        entry.StampButton = stampButton;
        entry.TearButton = tearButton;
        yourRow.Children.Add(stampButton);
        yourRow.Children.Add(tearButton);
        stack.Children.Add(yourRow);

        var peerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var peerStampButton = new Button { Content = "Peer stamps", FontSize = 10, Background = Mustard, Foreground = Ink };
        peerStampButton.Click += (_, _) => _ = OnPeerStampClickAsync(entry);
        var peerTearButton = new Button { Content = "Peer tears", FontSize = 10, Background = Mustard, Foreground = Ink };
        peerTearButton.Click += (_, _) => _ = OnPeerTearClickAsync(entry);
        peerRow.Children.Add(peerStampButton);
        peerRow.Children.Add(peerTearButton);
        stack.Children.Add(peerRow);

        return new Border
        {
            Child = stack,
            Background = Cream,
            BorderBrush = Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
        };
    }

    void UpdatePostcardCode(PostcardEntry entry, string formattedCode)
    {
        if (entry.SealText is not null) entry.SealText.Text = formattedCode;
        if (entry.StatusTextBlock is not null) entry.StatusTextBlock.Text = "Match this code with the other device, then decide.";
    }

    void OnLocalOutcome(PostcardEntry entry, string message, SolidColorBrush color)
    {
        if (entry.StatusTextBlock is not null)
        {
            entry.StatusTextBlock.Text = message;
            entry.StatusTextBlock.Foreground = color;
        }
        if (entry.StampButton is not null) entry.StampButton.IsEnabled = false;
        if (entry.TearButton is not null) entry.TearButton.IsEnabled = false;
    }

    async Task OnStampClickAsync(PostcardEntry entry)
    {
        await entry.LocalSession.ConfirmLocalAsync("Simulated Peer", CancellationToken.None).ConfigureAwait(false);
        _dispatcherQueue.TryEnqueue(() => ReflectLocalPhase(entry));
    }

    async Task OnTearClickAsync(PostcardEntry entry)
    {
        await entry.LocalSession.RejectLocalAsync(CancellationToken.None).ConfigureAwait(false);
    }

    async Task OnPeerStampClickAsync(PostcardEntry entry)
    {
        await entry.PeerSession.ConfirmLocalAsync("This Device", CancellationToken.None).ConfigureAwait(false);
    }

    async Task OnPeerTearClickAsync(PostcardEntry entry)
    {
        await entry.PeerSession.RejectLocalAsync(CancellationToken.None).ConfigureAwait(false);
    }

    void ReflectLocalPhase(PostcardEntry entry)
    {
        if (entry.LocalSession.Phase == PairingCeremonyPhase.LocalConfirmed && entry.StatusTextBlock is not null)
        {
            entry.StatusTextBlock.Text = "You stamped it. Waiting for the other side to confirm too...";
        }
    }

    void RenderRolodex()
    {
        RolodexPanel.Children.Clear();
        var peers = _identityStore.ApprovedPeers;
        NoApprovedPeersNotice.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var peer in peers)
        {
            RolodexPanel.Children.Add(BuildRolodexRow(peer));
        }
    }

    Border BuildRolodexRow(ApprovedPeer peer)
    {
        var row = new StackPanel { Spacing = 4 };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topRow.Children.Add(new TextBlock { Text = peer.Revoked ? "✉️" : "💻", VerticalAlignment = VerticalAlignment.Center });

        var nameBox = new TextBox { Text = peer.FriendlyName, Width = 180, Foreground = Ink, Background = Cream, IsEnabled = !peer.Revoked };
        topRow.Children.Add(nameBox);

        var renameButton = new Button { Content = "Rename", Background = Mustard, Foreground = Ink, IsEnabled = !peer.Revoked };
        renameButton.Click += (_, _) =>
        {
            _identityStore.Rename(peer.PeerId, nameBox.Text);
            RenderRolodex();
        };
        topRow.Children.Add(renameButton);

        var revokeButton = new Button { Content = peer.Revoked ? "Forgotten" : "Return to Sender", Background = Red, Foreground = Cream, IsEnabled = !peer.Revoked };
        revokeButton.Click += (_, _) =>
        {
            _identityStore.Forget(peer.PeerId);
            if (_liveTransportsByPeerId.TryGetValue(peer.PeerId, out var transport))
            {
                // Best-effort only (ADR-0002) — fire-and-forget, no retry/queueing.
                _ = transport.SendAsync(PairingFrameCodec.ForgottenFrame(Guid.NewGuid()), CancellationToken.None);
            }
            RenderRolodex();
        };
        topRow.Children.Add(revokeButton);
        row.Children.Add(topRow);

        if (peer.Revoked)
        {
            // ADR-0002: revocation is local and asymmetric — must stay
            // visible, not just silently drop the peer from the list.
            row.Children.Add(new TextBlock
            {
                Text = "You've forgotten this peer. They may still show you as approved on their device until they independently forget you too.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.75,
                Foreground = Red,
            });
        }

        return new Border
        {
            Child = row,
            Background = Cream,
            BorderBrush = Ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
        };
    }

    /// <summary>Deletes every demo peer's throwaway temp-dir IdentityStore
    /// created this dialog session (see <see cref="StartDemoCeremony"/>) —
    /// without this, "Simulate a new pairing request" would leak a new
    /// %TEMP%\IntercomPairingDemo_* folder on every use. Best-effort: a
    /// locked/already-gone directory must not prevent the dialog from
    /// closing.</summary>
    void CleanUpDemoPeerStores()
    {
        foreach (var entry in _postcards)
        {
            try
            {
                if (Directory.Exists(entry.PeerStoreDir)) Directory.Delete(entry.PeerStoreDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class PostcardEntry(PairingCeremonyCoordinator localSession, PairingCeremonyCoordinator peerSession, string peerStoreDir)
    {
        public PairingCeremonyCoordinator LocalSession { get; } = localSession;
        public PairingCeremonyCoordinator PeerSession { get; } = peerSession;
        public string PeerStoreDir { get; } = peerStoreDir;
        public Border? CardBorder { get; set; }
        public TextBlock? SealText { get; set; }
        public TextBlock? StatusTextBlock { get; set; }
        public Button? StampButton { get; set; }
        public Button? TearButton { get; set; }
    }
}
