using Intercom.AttentionCards;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Intercom.App.AttentionCards;

/// <summary>
/// Issue #25: presents one attention card as a native Windows toast
/// (<see cref="AppNotificationManager"/>) — the MVP-default presentation per
/// docs/mvp-specification.md §6 and the attention-card-presentation-prototype.html
/// comparison, in preference to a custom always-on-top popup window (never
/// built here, by design).
///
/// API usage verified against Microsoft Learn (not assumed from memory,
/// since this is a brand-new API surface for this codebase):
/// "Quickstart: Send and Handle App Notifications" and "App notification
/// content" (both fetched 2026-07-27). Key facts this class relies on:
/// <list type="bullet">
/// <item><see cref="AppNotificationManager.Register"/> must be called before
/// the app reads its own activation args (<c>AppInstance.GetActivatedEventArgs</c>)
/// — see the important caveat in this class's remarks below about
/// <c>Program.cs</c> already violating that ordering for THIS app's
/// single-instance bootstrap.</item>
/// <item>A notification click while the app is already running raises
/// <see cref="AppNotificationManager.NotificationInvoked"/>; Windows App SDK
/// apps are always launched in the foreground for a COM (cold-launch)
/// activation, but the docs are explicit that "your app can call
/// GetActivatedEventArgs to detect if the activation was launched by a
/// notification and determine ... whether to fully launch the foreground app
/// or just handle the notification and exit" — i.e. even though the process
/// itself is foregrounded, the MAIN WINDOW is a separate, app-controlled
/// decision. That's what makes "send exactly one acknowledgement without
/// forcing the main window open" achievable at all.</item>
/// <item>Nothing in the docs guarantees <see cref="AppNotificationManager.NotificationInvoked"/>
/// fires on the UI thread — the quickstart's own sample marshals through
/// <c>DispatcherQueue.TryEnqueue</c> before touching UI state, which this
/// class does too, mirroring App.xaml.cs's existing
/// <c>_uiDispatcherQueue.TryEnqueue</c> pattern for discovery/activation
/// callbacks that are documented as arriving off the UI thread.</item>
/// <item>App notifications are not supported for elevated apps (irrelevant
/// here — ADR-0003/the research doc already commit this app to running
/// medium-integrity, never elevated).</item>
/// </list>
///
/// <para><b>Known limitation, called out for review</b>: <c>Program.cs</c>'s
/// existing single-instance bootstrap (issue #18) already calls
/// <c>AppInstance.GetCurrent().GetActivatedEventArgs()</c> at the very top of
/// <c>Main</c>, before <see cref="App"/> is even constructed — i.e. strictly
/// before <see cref="Initialize"/> below can call <see cref="AppNotificationManager.Register"/>.
/// Reordering that foundational single-instancing code was judged out of
/// scope for this ticket (high blast radius, unrelated to attention cards).
/// The practical consequence: this presenter correctly handles the
/// realistic, primary scenario — the app is already resident (per
/// ADR-0003, Intercom is designed to always be running in the tray) and
/// <see cref="AppNotificationManager.NotificationInvoked"/> fires normally.
/// The COLD-launch-via-notification-click path (the process isn't running at
/// all — e.g. after a crash) is best-effort only: <see cref="App.OnLaunched"/>
/// still checks <c>AppInstance.GetCurrent().GetActivatedEventArgs()</c>
/// itself and forwards an <c>AppNotification</c>-kind activation to
/// <see cref="HandleActivation"/>, but since Register() hasn't necessarily
/// run before Windows resolved that activation kind, this path is UNVALIDATED
/// — same "documented, not silently assumed to work" honesty bar as
/// <c>SslPeerConnection</c>/<c>Win32DnsServiceDiscovery</c>'s real-hardware
/// caveats from earlier tickets.</para>
/// </summary>
public sealed class AttentionCardToastPresenter : IDisposable
{
    readonly DispatcherQueue _uiDispatcherQueue;
    bool _registered;

    /// <summary>Raised when the human clicked the toast's Acknowledge button
    /// — the ONLY toast action with real wire semantics (ADR-0001). Always
    /// raised on the UI thread (see class remarks). The caller is
    /// responsible for actually sending the Acknowledged frame (via
    /// <see cref="AttentionCardService.AcknowledgeAsync"/>) and must NOT
    /// force the main window open in response — that's the acceptance
    /// criterion this event exists to satisfy.</summary>
    public event Action<Guid>? AcknowledgeRequested;

    /// <summary>Raised when the toast BODY (not a button) was clicked — the
    /// ordinary "bring the app to the foreground" activation. Always raised
    /// on the UI thread.</summary>
    public event Action<Guid>? OpenRequested;

    public AttentionCardToastPresenter(DispatcherQueue uiDispatcherQueue)
    {
        _uiDispatcherQueue = uiDispatcherQueue;
    }

    /// <summary>Registers this process for notification activation and wires
    /// <see cref="AppNotificationManager.NotificationInvoked"/>. Must be
    /// called once, as early as possible in <c>App.OnLaunched</c> — see this
    /// class's remarks for the known ordering caveat versus <c>Program.cs</c>.</summary>
    public void Initialize()
    {
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
        _registered = true;
    }

    /// <summary>Builds and shows one native toast for <paramref name="card"/>.
    /// Callers are responsible for the DND gate (docs/research/windows-
    /// resident-app.md: "App-level Do Not Disturb should suppress creation
    /// of chime/toast notifications" — this method IS that creation, so it
    /// must never be called while DND is suppressing
    /// <c>InteractionKind.AttentionChime</c>; the card itself must still be
    /// queued/delivered regardless, which happens one layer down in
    /// <see cref="AttentionCardService"/> and is never gated by this
    /// class).</summary>
    public void Show(AttentionCard card, string fromLabel)
    {
        var cardId = card.MessageId.ToString();

        var builder = new AppNotificationBuilder()
            .AddArgument("cardId", cardId)
            .AddArgument("action", "Open")
            // The research doc's proposed 320x320 illustrated hero art is a
            // future DESIGN asset this ticket doesn't ship (no illustrator
            // pass done here) — the packaged app logo stands in so the real
            // SetHeroImage API path is still genuinely exercised end to end,
            // per docs/research/windows-resident-app.md's own point that the
            // asset is a source size, not a guaranteed rendered square, and
            // Windows owns the actual toast layout regardless of what image
            // is supplied.
            .SetHeroImage(new Uri("ms-appx:///Assets/Square150x150Logo.png"))
            .AddText($"{card.Icon}  {card.Purpose}")
            .AddText($"from {fromLabel}")
            .SetAudioEvent(AppNotificationSoundEvent.Reminder)
            .AddButton(new AppNotificationButton("Acknowledge")
                .AddArgument("cardId", cardId)
                .AddArgument("action", "Acknowledge"))
            .AddButton(new AppNotificationButton("Snooze")
                .AddArgument("cardId", cardId)
                .AddArgument("action", "Snooze"))
            .AddButton(new AppNotificationButton("Dismiss")
                .AddArgument("cardId", cardId)
                .AddArgument("action", "Dismiss"));

        AppNotificationManager.Default.Show(builder.BuildNotification());
    }

    /// <summary>Handles an activation this process observed itself via
    /// <c>AppInstance.GetActivatedEventArgs()</c> at cold-launch time — see
    /// this class's remarks for why that path is best-effort/unvalidated.
    /// Foreground clicks while already running arrive via
    /// <see cref="OnNotificationInvoked"/> instead, which funnels into this
    /// same logic.</summary>
    public void HandleActivation(AppNotificationActivatedEventArgs args) => Handle(args);

    void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Handle(args);

    void Handle(AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;
        if (arguments is null) return;
        if (!arguments.TryGetValue("cardId", out var cardIdText) || !Guid.TryParse(cardIdText, out var cardId)) return;
        var action = arguments.TryGetValue("action", out var a) ? a : "Open";

        // Never assume which thread this arrived on (see class remarks) —
        // always marshal before touching anything UI-adjacent, mirroring
        // App.xaml.cs's existing _uiDispatcherQueue.TryEnqueue pattern.
        _uiDispatcherQueue.TryEnqueue(() =>
        {
            switch (action)
            {
                case "Acknowledge":
                    AcknowledgeRequested?.Invoke(cardId);
                    break;

                case "Snooze":
                case "Dismiss":
                    // Deliberately local-only (issue #25 scope note): neither
                    // ADR-0001 nor the attention-card-presentation prototype
                    // defines any wire semantics for Snooze/Dismiss — only
                    // Acknowledge is a real protocol receipt. Both buttons
                    // simply let the toast close; no frame is sent, no local
                    // card state changes, and the main window is never
                    // forced open. A real "snooze and re-notify later" timer
                    // is out of scope for this ticket.
                    break;

                default: // toast body clicked ("Open") — bring the app forward
                    OpenRequested?.Invoke(cardId);
                    break;
            }
        });
    }

    /// <summary>Unregisters this process from notification activation —
    /// called from <c>App.OnQuitRequested</c>, mirroring this app's existing
    /// careful <c>Dispose()</c> discipline (<c>TrayIcon</c>,
    /// <c>DiscoveryService</c>, <c>SessionMessagePump</c>).</summary>
    public void Dispose()
    {
        if (!_registered) return;
        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.Unregister();
        _registered = false;
    }
}
