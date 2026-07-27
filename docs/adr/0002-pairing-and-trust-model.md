# Lock the MVP pairing and trust model: approval, verification, storage, revocation, re-pairing

Resolves [issue #11](https://github.com/Allann/Intercom/issues/11), building on the pairing-security research (`docs/research/pairing-security.md`) and the Postcard Badges pairing-ceremony prototype ([issue #7](https://github.com/Allann/Intercom/issues/7)).

## Decision

**Verification code.** The reviewed six-digit numeric SAS (SHA-256 over the canonical pairing transcript, rejection-sampled to `000000`-`999999`) is the locked wire format — no separate encoding was invented for the UI. The Postcard Badges prototype's wax-seal graphic displays this same six-digit value formatted as `NNN NNN`, not a stylized alphanumeric string.

**Pairing request lifecycle.** A pairing request expires after **2 minutes** if not confirmed on both sides; on expiry it is simply removed with no trust state change. An explicit reject action is supported and sent immediately over the still-open pairing connection (distinct from passively letting it expire) — both sides are guaranteed live during the ceremony, unlike the forget-peer case below.

**Rate limiting.** One outstanding pairing request per peer identity; 5 attempts per source per 10-minute window; 20 attempts globally per 10-minute window. Existing approved-peer connections are unaffected by these limits — they only throttle *new* pairing attempts.

**Identity storage.** Unchanged from the research doc: one long-lived ECDSA P-256 self-signed identity certificate per peer, protected with Windows DPAPI (`CurrentUser` scope). Approval pins the peer by SHA-256 hash of its SPKI, never by friendly name, IP, or certificate subject. Certificate lifetime is **10 years**, and the MVP has **no renewal mechanism** — expiry is treated identically to identity loss (generate a new identity, re-pair everywhere) rather than building and testing a same-key renewal ceremony for a problem that won't occur during the MVP's realistic lifetime.

**Revocation ("forget peer").** Local and immediate: terminate active sessions, remove the SPKI pin/contact association, delete pending session keys and replay state. A best-effort `Forgotten` notice is sent to the other peer only if the connection happens to be live at that instant — no queued or guaranteed delivery, consistent with the no-mailbox delivery semantics already locked in ADR-0001. Forgetting stays asymmetric: the other side remains approved until it independently forgets you too, and this asymmetry must stay visible in the UI.

**Re-pairing and identity change.** Unchanged from the research doc: a pin mismatch on an approved peer never falls back to pairing within the same connection — the UI must show "identity changed" and require an explicit forget/re-pair action. A lost or corrupted local identity generates a new identity and requires re-pairing with everyone; the old approved-peer records are never silently carried forward.

## Why

Every number chosen here (2-minute expiry, the specific rate-limit thresholds, the 10-year cert lifetime) trades an unlikely edge case for not building infrastructure a household deployment won't need for years: renewal ceremonies, guaranteed-delivery revocation notices, and aggressive anti-abuse tooling all cost real implementation and test surface that isn't justified by the threat model this project already accepted (a small trusted family group, not an adversarial multi-tenant service).

## Considered and rejected

- **A shorter certificate lifetime with a real renewal mechanism** — rejected; renewal requires proving a new certificate belongs to the same pinned key without weakening the pin model, which is real security-sensitive work for a risk (routine cert expiry during a household MVP's life) that a 10-year validity period makes moot.
- **A custom/stylized code encoding for the wax-seal UI** distinct from the six-digit SAS — rejected; the six-digit construction is the one that has been reviewed and is meant to get fixed transcript test vectors, and inventing a second unreviewed transform to look more "on brand" isn't worth the risk for a cosmetic gain.
- **Guaranteed/queued delivery of the forget-peer notice** — rejected; it would require a store-and-forward mechanism this project has deliberately avoided everywhere else (ADR-0001's no-mailbox delivery semantics), for a notice whose absence the other side would discover anyway on its next connection attempt.
- **Silent-only rejection during pairing (no explicit reject signal)** — rejected in favor of an explicit, immediate reject, since both sides are already live on the same connection during the ceremony and an ambiguous 2-minute stall is worse UX than a clear "they said no."
