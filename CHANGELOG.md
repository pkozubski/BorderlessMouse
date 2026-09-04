# Changelog

## Unreleased

### Fixed

- macOS: the Permissions page now really checks the system-audio-recording consent instead of
  mirroring the streaming state, so it no longer reports "missing" whenever no stream is running.
- macOS: added **Request** — the app can trigger the consent prompt itself, without waiting for a
  Windows peer to start a stream.
- macOS: added **Repair**, which clears a stale TCC entry
  (`tccutil reset AudioCapture com.borderlessmouse.mac`) and asks again. Needed when System
  Settings shows the permission as granted but macOS denies it, which happens after the app's
  code signature changes (ad-hoc local build ↔ signed release).

## 2.0.0 — 2026-09-03 — free beta

### Security

- Replaced unauthenticated protocol v1 with mutually authenticated protocol v2.
- Added a 128-bit Base32 pairing code stored in Keychain and DPAPI.
- Encrypted all control and clipboard frames with directional AES-256-GCM keys.
- Encrypted audio packets and added source, size, session and replay validation.
- Prevented unauthenticated connections from replacing an active session.
- Bounded the Windows send queue and fixed framed PONG responses.
- Made checksums and project-owned ECDSA signatures mandatory for all updates.

### Product

- Added guided first-run onboarding on macOS and Windows.
- Added pairing management and immediate access revocation.
- Added Polish/English system-language localization, including runtime diagnostics.
- Added a configurable emergency control shortcut: Scroll Lock, Pause/Break or F12.
- Simplified the macOS connection screen and moved ports to advanced details.
- Increased Windows control hit areas for accessibility.

### Distribution

- Enabled the macOS hardened runtime.
- Restricted certificate use to the release workflow.
- Added a zero-cost beta release path with explicit first-launch trust warnings.
- Reserved Apple notarization and trusted Windows Authenticode for the public commercial release.
- Added download size limits and rollback-safe updater replacement.
- Locked the Windows NuGet dependency graph for reproducible CI and release restores.
- Bundled complete third-party notices, including Inter OFL and ANGLE terms, with release artifacts.
