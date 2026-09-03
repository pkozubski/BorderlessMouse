# Security policy

BorderlessMouse handles keyboard input, clipboard contents and system audio.
Please do not publish vulnerability details in a public issue.

## Reporting

Use GitHub's **Report a vulnerability** form in the repository Security tab.
Include the affected version, operating systems, reproduction steps and logs
with personal data removed. You should receive an initial response within five
business days during the beta period.

## Supported versions

Only the newest beta or stable release receives security fixes. Protocol v1 is
unsupported because it did not authenticate or encrypt LAN traffic.

## Design guarantees in protocol v2

- A 128-bit pairing secret is stored in macOS Keychain and Windows DPAPI.
- Control, clipboard and status frames use AES-256-GCM with directional keys.
- Audio packets are authenticated, encrypted, source-bound and replay-protected.
- A new unauthenticated connection cannot replace an active session.
- Update installation requires a checksum and a pinned publisher signature.

See `PROTOCOL.md` for the wire format and trust boundaries.
