# Changelog

## Unreleased — commercial beta foundation

### Security

- Replaced unauthenticated protocol v1 with mutually authenticated protocol v2.
- Added a 128-bit Base32 pairing code stored in Keychain and DPAPI.
- Encrypted all control and clipboard frames with directional AES-256-GCM keys.
- Encrypted audio packets and added source, size, session and replay validation.
- Prevented unauthenticated connections from replacing an active session.
- Bounded the Windows send queue and fixed framed PONG responses.
- Made checksums and pinned publisher signatures mandatory for Windows updates.

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
- Added required macOS notarization and Windows Authenticode signing steps.
- Added download size limits and rollback-safe updater replacement.
- Locked the Windows NuGet dependency graph for reproducible CI and release restores.
- Bundled complete third-party notices, including Inter OFL and ANGLE terms, with release artifacts.
