# Commercial release runbook

## One-time setup

1. Create and protect the GitHub environment `production-release`; require a
   reviewer and restrict it to version tags.
2. Obtain an Apple Developer ID Application certificate. Replace
   `macos/BorderlessMouse/Resources/ReleaseSigning.cer` with its public leaf
   certificate in DER form.
3. Add `MACOS_SIGNING_P12_BASE64`, `MACOS_SIGNING_P12_PASSWORD`, `APPLE_ID`,
   `APPLE_TEAM_ID` and `APPLE_APP_PASSWORD` as environment secrets.
4. Obtain a publicly trusted Windows code-signing certificate and add
   `WINDOWS_SIGNING_PFX_BASE64` and `WINDOWS_SIGNING_PFX_PASSWORD`.
5. Have counsel/accounting approve the EULA, privacy notice, refund policy,
   seller identity, VAT and merchant-of-record configuration.

## Release gate

- CI is green on macOS and Windows.
- A Windows 10/11 and macOS 14/15/26 smoke test covers pairing, reconnect,
  clipboard, input, audio, sleep/wake and update rollback.
- Version notes identify beta limitations and protocol incompatibilities.
- The dependency inventory and third-party notices are current.
- `THIRD_PARTY_NOTICES.txt` is present beside the Windows executable and inside
  the macOS application resources.
- No signing secret is available to pull-request or ordinary branch workflows.

Create an annotated `vX.Y.Z` tag. The release workflow refuses to publish if
signing or notarization credentials are missing, if notarization fails, or if
Authenticode verification fails.
