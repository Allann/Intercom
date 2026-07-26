# Prototype: global push-to-talk input + attention card presentation (issue #16)

Issue #16 has two independent halves. Both got a prototype; only one is safe
to actually validate without a person present.

## Part 1 — global hold/release detection (this folder, real runnable code)

`Program.cs` is a genuine Win32 console app, not a mockup. It:

- Registers a real system-wide hotkey (`Ctrl+Alt+Space`) with `RegisterHotKey`.
- Detects release with a narrowly-scoped `GetAsyncKeyState` poll on just that
  one key, at a 15ms interval — approach **(a)** from
  `docs/research/windows-resident-app.md`'s three candidates (the others
  being raw input and a `WH_KEYBOARD_LL` hook thread), chosen because it's
  the least invasive: no keystroke suppression, no global hook, no observing
  keys other than the one configured.
- Uses `MOD_NOREPEAT` so Windows' own key-repeat doesn't refire the hotkey
  while held.
- Unregisters cleanly on exit (`quit` + Enter).

**Run it:** `dotnet run`. It registered and unregistered cleanly during this
session (confirmed: `RegisterHotKey` succeeded, no Win32 error, clean exit on
`quit`). What it has **not** been validated against yet is an actual human
holding and releasing the key — nobody was here to do that. **Do that first
thing tomorrow**: hold `Ctrl+Alt+Space`, watch for the `PTT DOWN` line,
release it, confirm `PTT UP` prints promptly with a sane held-duration.

If `RegisterHotKey` fails on your machine (another app already owns that
combination, or your Windows build reserves it), the console prints the
Win32 error and exits — change `VK_SPACE`/`MOD_*` at the top of `Program.cs`
and re-run.

### What this does NOT prove yet

Per the research doc's own acceptance checks, still needed before treating
push-to-talk input as settled:

1. Real hold/release timing under load (is 15ms polling responsive enough,
   or does it need tightening?).
2. Comparison against raw input and a dedicated `WH_KEYBOARD_LL` hook thread
   — this prototype only implements the least-invasive candidate; the doc
   asks for a comparison, not just a pick.
3. Conflicting global shortcut handling (register a combo something else
   already owns and confirm the failure is clear, not silent).
4. Lock-screen/session-change behavior, and recovery after both.
5. Accessibility — does this interact acceptably with any assistive input
   tooling someone in the household might use?

## Part 2 — attention card presentation (repo root, HTML mockup)

`attention-card-presentation-prototype.html` (repo root) compares **native
Windows toast** vs. a **custom always-on-top popup window** for showing the
320×320 attention card, since the research doc flags this as a real
trade-off: the 320×320 illustration is only an *asset source size* under a
native toast — Windows owns actual layout — while a custom popup can
guarantee the comic-card look at the cost of losing Action Center
integration and requiring its own accessibility/focus/multi-monitor work.

Open it in a browser, flip between the two with the floating switcher. It's
a visual comparison only — no code behind it, no real `AppNotificationManager`
call. Recommendation in the mockup itself: default to native toast (durable,
accessible, free Action Center integration), and only build the custom
popup if the comic-card visual is judged essential after seeing both at real
Windows DPI/text scales — which is the research doc's own suggested next
step, still unprototyped for real.
