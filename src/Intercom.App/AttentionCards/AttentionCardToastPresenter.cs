using System.Text;
using Intercom.AttentionCards;
using Intercom.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics.Imaging;
using Windows.Storage;

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
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            // Registration is transactional: if Windows rejects the COM/
            // notification registration, don't leave a handler attached to
            // the singleton manager for a presenter the app cannot use.
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            throw;
        }
    }

    /// <summary>Builds and shows one native toast for <paramref name="card"/>.
    /// Callers are responsible for the DND gate (docs/research/windows-
    /// resident-app.md: "App-level Do Not Disturb should suppress creation
    /// of chime/toast notifications" — this method IS that creation, so it
    /// must never be called while DND is suppressing
    /// <c>InteractionKind.AttentionChime</c>; the card itself must still be
    /// queued/delivered regardless, which happens one layer down in
    /// <see cref="AttentionCardService"/> and is never gated by this
    /// class).
    ///
    /// <para>Issue #34: rather than commissioning bespoke illustrated hero
    /// art (a design asset this codebase has never shipped), the toast's
    /// logo is the SAME per-card emoji glyph already used everywhere else a
    /// card is shown — the composer's icon picker and
    /// <c>MainWindow.BuildAttentionCardTile</c>'s shelf tile both render
    /// <see cref="AttentionCard.Icon"/> as a plain text glyph, so the toast
    /// does the same, just rasterized (toast logos are images, not text) via
    /// <see cref="RenderIconAsync"/>.</para></summary>
    public Task<AttentionCardNotificationSubmission> ShowAsync(AttentionCard card, string fromLabel)
    {
        var submission = AttentionCardNotificationSubmission.From(MapSetting(AppNotificationManager.Default.Setting));
        if (submission.Status == AttentionCardNotificationSubmissionStatus.Blocked)
        {
            DiagnosticLog.Current.Info(
                "notifications.show-blocked",
                $"card={card.MessageId} setting={submission.Setting}");
            return Task.FromResult(submission);
        }

        var cardId = card.MessageId.ToString();

        var builder = new AppNotificationBuilder()
            .AddArgument("cardId", cardId)
            .AddArgument("action", "Open")
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

        DiagnosticLog.Current.Info("notifications.show-requested", $"card={card.MessageId}");
        AppNotificationManager.Default.Show(builder.BuildNotification());
        DiagnosticLog.Current.Info(
            "notifications.show-submitted",
            $"card={card.MessageId} setting={submission.Setting}");
        return Task.FromResult(submission);
    }

    static AttentionCardNotificationSetting MapSetting(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled => AttentionCardNotificationSetting.Enabled,
        AppNotificationSetting.DisabledForApplication => AttentionCardNotificationSetting.DisabledForApplication,
        AppNotificationSetting.DisabledForUser => AttentionCardNotificationSetting.DisabledForUser,
        AppNotificationSetting.DisabledByGroupPolicy => AttentionCardNotificationSetting.DisabledByGroupPolicy,
        AppNotificationSetting.DisabledByManifest => AttentionCardNotificationSetting.DisabledByManifest,
        AppNotificationSetting.Unsupported => AttentionCardNotificationSetting.Unsupported,
        _ => AttentionCardNotificationSetting.Unsupported,
    };

    /// <summary>Rasterizes <paramref name="icon"/> (a single emoji glyph, per
    /// <see cref="Intercom.AttentionCards.AttentionCardPresets"/>) into a
    /// small PNG under the app's local data folder, so it can be referenced
    /// by a <c>file://</c> URI — <see cref="AppNotificationBuilder"/> only
    /// accepts image URIs, never a raw string glyph. Results are cached on
    /// disk per distinct glyph (the icon set is small and fixed), keyed by
    /// the glyph's own codepoints so the filename is always filesystem-safe
    /// without needing a lookup table.</summary>
    static async Task<Uri?> RenderIconAsync(string icon)
    {
        try
        {
            var iconsFolder = await ApplicationData.Current.LocalFolder
                .CreateFolderAsync("ToastIcons", CreationCollisionOption.OpenIfExists);
            var fileName = CodepointFileName(icon);
            var file = await iconsFolder.CreateFileAsync(fileName, CreationCollisionOption.OpenIfExists);

            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size == 0)
            {
                await RasterizeAsync(icon, file);
            }

            return new Uri(file.Path);
        }
        catch (Exception)
        {
            // Best-effort only: a glyph the font can't render, a locked-down
            // LocalFolder, etc. should never block the toast itself — it
            // just falls back to no logo image (the icon is still visible in
            // the toast's text line above).
            return null;
        }
    }

    static async Task RasterizeAsync(string icon, StorageFile file)
    {
        const int size = 128;

        var border = new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = icon,
                FontSize = size * 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        border.Measure(new Windows.Foundation.Size(size, size));
        border.Arrange(new Windows.Foundation.Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(border, size, size);
        var pixels = await bitmap.GetPixelsAsync();

        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
    }

    static string CodepointFileName(string icon)
    {
        var sb = new StringBuilder();
        foreach (var rune in icon.EnumerateRunes())
        {
            if (sb.Length > 0) sb.Append('-');
            sb.Append(rune.Value.ToString("x"));
        }
        sb.Append(".png");
        return sb.ToString();
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
        var action = arguments.TryGetValue("action", out var a) ? a : "Open";
        var cardId = Guid.Empty;
        var hasCardId = arguments.TryGetValue("cardId", out var cardIdText) && Guid.TryParse(cardIdText, out cardId);
        if (action != "Open" && !hasCardId) return;

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
