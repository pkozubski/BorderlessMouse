# Changelog

## Unreleased

### Added

- **Mac virtual display on Windows** (experimental). macOS creates a driverless virtual display
  (`CGVirtualDisplay`) next to the edge facing Windows, captures it with ScreenCaptureKit,
  encodes it with hardware H.264 (VideoToolbox, low-latency rate control) and streams it over
  a separate, one-time TCP connection encrypted with a per-stream AES-256-GCM key. Windows
  decodes it with Media Foundation on the GPU (software fallback) and shows it full screen on
  the monitor next to the Mac while the pointer is on the virtual display.
- Protocol: `DISPLAY_START/STOP/READY/KEYFRAME/FOCUS` (0x80–0x84), new `STATUS` bits and an
  optional flags byte in `LEAVE`. Windows only uses them when the Mac advertises support, so
  mixed versions keep working.
- macOS: Screen Recording permission row, "Share a virtual display" toggle and stream status;
  `--display-selftest` diagnostic.
- Windows: "Mac display" card with a toggle, "show it whenever you control the Mac" option,
  status, throughput and restart.
- **Window mode** (default): every Mac window dragged onto the virtual display becomes a real
  Windows window styled like macOS — rounded corners, the Mac window's own title bar, and a dark
  macOS-style menu bar above it with the app's menu (read through Accessibility, with shortcuts
  and disabled/checked states) instead of the Mac menu bar; dragging and double-click maximize
  from that bar (the Mac window is resized to match); tiny helper windows are skipped; taskbar button with the Mac
  app icon, Alt+Tab, normal z-order, closing closes it on the Mac. Each window is captured independently (ScreenCaptureKit) and
  streamed separately, so moving it never reveals the wallpaper and corners are rounded like on
  macOS. The real Windows pointer drives it (absolute positions, clicks also activate the
  Windows window), the keyboard follows the active window with Windows shortcuts kept local,
  dropping a window hands the pointer to Windows, dragging it across the Mac-side edge returns
  it to the MacBook. Windows restored by macOS from a previous session are moved back to the
  MacBook. Nothing is captured until a window is moved (not even the whole virtual display).
  New messages `MOUSE_ABSOLUTE`, `WINDOW_ENTER/LEAVE/HANDOFF/RAISE/CLOSE`,
  `DISPLAY_MODE`, `DISPLAY_WINDOWS`, `WINDOW_ICON`, per-window video frames and STATUS bit 6.
- Local macOS builds are signed with the Apple Development identity when available, so
  privacy permissions survive rebuilds.
- The virtual display is entered only while dragging (a mouse button is held); plain pointer
  movement across that edge still returns to Windows.
- Windows: `Ctrl + Alt + Shift + B` emergency shortcut for compact keyboards without
  Scroll Lock or Pause.
- Shared test vectors (CryptoKit ↔ .NET) and an H.264 fixture encoded on macOS that the Windows
  checks decrypt and, on Windows, decode.

## 2.0.2 — 2026-09-04

### Fixed

- Windows: audio never started since 2.0.0. The control socket is dual-stack, so an IPv4 Mac is
  reported as `::ffff:192.168.x.y`, and the source-address check added in 2.0.0 rejected it with
  "Cannot open UDP port: cannot determine the Mac IPv4 address". `AUDIO_START` was therefore never
  sent, which is also why macOS never asked for the audio permission. Mapped addresses are now
  normalized to IPv4, and a genuine IPv6 session says so instead of hiding behind a parameter name.

## 2.0.1 — 2026-09-04

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
