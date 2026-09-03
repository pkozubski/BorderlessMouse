# Privacy policy (beta)

Effective date: 3 September 2026

BorderlessMouse is a local desktop utility. The application does not create an
account, display advertising, include analytics SDKs or send usage telemetry.

## Data processed locally

When enabled, BorderlessMouse processes keyboard and mouse events, clipboard
text or images, device names, local IP addresses and system audio. These data
are transmitted only between the paired Windows computer and Mac on the local
network. The application does not intentionally send their contents to the
developer or a cloud service.

The pairing secret is stored in macOS Keychain or encrypted for the current
Windows account using DPAPI. Application preferences and recent diagnostic log
entries are stored locally.

## External network requests

If update checks are enabled, the application contacts the GitHub API and
GitHub release hosting. GitHub receives ordinary connection metadata such as
the public IP address and user agent. See GitHub's privacy statement for its
processing. No clipboard, input or audio content is included in update checks.

## Diagnostics

Logs remain on the device unless the user chooses to copy and send them. Users
should remove device names, IP addresses and other personal information before
sharing a diagnostic report.

## Contact and changes

Privacy questions can be submitted through the repository's private GitHub
Security Advisory form. This policy must be updated before adding telemetry,
online licensing, accounts or any other external service.
