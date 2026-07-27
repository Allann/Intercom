# Lock installation, startup, and update boundaries

Resolves [issue #12](https://github.com/Allann/Intercom/issues/12), building on the Windows resident-app research (`docs/research/windows-resident-app.md`), which had already decided packaged MSIX identity but explicitly left distribution/signing/updates open.

## Decision

**Distribution and signing.** Self-signed identity certificate, sideloaded MSIX, trusted to `Cert:\CurrentUser\TrustedPeople` via PowerShell — not `LocalMachine`, so no administrator elevation is required. This is a correction to a loose assumption in the earlier research doc: "no administrator setup" and "no special install" are compatible with a self-signed sideload as long as the trust step targets the current-user certificate store rather than the machine-wide one.

**Launch-at-sign-in.** Enabled automatically during first-run setup with no yes/no prompt — installing an always-reachable household intercom already implies wanting it to start with Windows, so asking again is redundant friction. A passive confirmation line communicates it; a Settings toggle allows disabling later; the app always reflects actual OS state (enabled / user-disabled / policy-disabled) rather than assuming its own request is authoritative.

**Tray lifecycle.** Closing the main window hides it; an explicit tray action is required to quit (unchanged from the research doc). No first-run "still running in the tray" notice — this pattern is now a common enough convention (browsers, chat apps) that a dedicated explanation is unnecessary noise.

**Permissions onboarding.** One combined onboarding screen at first-run setup requests microphone access and notification permission together, Teams-style. LAN discovery is deliberately started at that same moment so the Windows Firewall private-network prompt — which the app cannot directly control the timing of — naturally clusters into the same first-run moment rather than surprising the user later. The app's own settings screen deep-links to the relevant OS Settings pages (microphone privacy, notifications, Windows Defender Firewall's allowed-apps list) for later review, and reflects actual granted/denied state for each rather than only remembering its own onboarding request.

**Manual updates.** A lightweight, non-blocking in-app check against a static version source (e.g. a GitHub Releases API or a static JSON manifest) surfaces a passive "update available" notice only — never an automatic download or silent install. Applying an update means re-running the newly downloaded, identically-signed MSIX, which upgrades in place under the same package identity and preserves local data/settings.

**Crash recovery.** Manual relaunch only; no watchdog, scheduled-task restart, or other auto-recovery mechanism. The only addition is visibility: a simple "closed unexpectedly last time" notice on the next manual launch, so a crash isn't a silent mystery, without building infrastructure to paper over it.

## Why

Every choice here trades a small amount of user friction or manual attention for not building infrastructure a household-scale, single-maintainer project can't justify: no developer-account/store-review pipeline, no paid code-signing certificate, no watchdog process, no auto-update pipeline with its own failure modes. The one deliberate exception — clustering the firewall prompt into onboarding by starting discovery early — costs nothing extra and meaningfully improves the first-run experience.

## Considered and rejected

- **Microsoft Store distribution** — rejected; free automatic updates and zero-trust-step installs are real benefits, but a developer account, store review process, and losing control over update *timing* (the issue explicitly asks for a manual workflow) outweigh them for a family-scale project.
- **Purchased code-signing certificate** — rejected; removes the one-time trust step, but costs real recurring money and requires CA identity verification for a project where the free self-signed + CurrentUser-store path already satisfies "no administrator setup."
- **Prompting again for launch-at-sign-in during onboarding** — rejected as redundant: the decision to install an always-reachable intercom already implies the answer.
- **A first-run "still running in the tray" notice** — rejected as unnecessary given how common the minimize-to-tray convention already is.
- **Requesting permissions contextually at first real use, scattered across the first session** — rejected in favor of one combined onboarding moment, matching a pattern (Teams-style upfront permissions) already familiar to users.
- **A lightweight auto-restart/watchdog mechanism for crash recovery** — rejected; adds real complexity and its own failure modes for a scenario (rare crash, simple manual relaunch) that doesn't justify it at MVP scale.
