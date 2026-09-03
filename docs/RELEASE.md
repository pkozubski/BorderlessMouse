# Release runbook

## Current free beta

The free beta deliberately does not require paid Apple notarization or a publicly
trusted Windows Authenticode certificate. Users receive an operating-system warning
on first launch, but update integrity is not disabled:

- both artifacts require SHA-256 and a detached ECDSA P-256 signature;
- the update public key is embedded in both applications;
- the private key exists only as `UPDATE_SIGNING_PRIVATE_KEY_PEM` in the protected
  GitHub environment and as a recovery copy in the maintainer's macOS Keychain;
- macOS additionally uses the existing stable self-signed code-signing identity so
  updates keep a consistent bundle identity and TCC permissions.

Never place the update private key in the repository, workflow logs or release
assets. Rotating it requires a transition release signed by the previous key.

## Free-beta release gate

- CI is green on macOS and Windows.
- A Windows 10/11 and macOS 14/15/26 smoke test covers pairing, reconnect,
  clipboard, input, audio, sleep/wake and update rollback.
- Version notes identify the first-launch trust warning and protocol incompatibility.
- `UPDATE_SIGNING_PRIVATE_KEY_PEM`, `MACOS_SIGNING_P12_BASE64` and
  `MACOS_SIGNING_P12_PASSWORD` are available to `production-release`.
- The dependency inventory and third-party notices are current.
- No signing secret is available to pull-request or ordinary branch workflows.

Create an annotated `vX.Y.Z` tag. The workflow refuses to publish if the macOS
identity is missing, the project update key does not match the embedded public key,
or either generated artifact signature fails verification.

## Future public commercial release

Before removing the beta warning from product messaging:

1. Obtain Apple Developer Program membership, replace the self-signed identity with
   a Developer ID Application certificate, and restore notarization plus stapling.
2. Configure `APPLE_ID`, `APPLE_TEAM_ID` and `APPLE_APP_PASSWORD` or App Store
   Connect API-key credentials.
3. Use a publicly trusted Windows signing service such as Microsoft Artifact
   Signing and integrate it through OIDC. Do not export a modern public signing key
   into the repository or a plain PFX secret.
4. Verify Authenticode, Gatekeeper and notarization after packaging while keeping
   the existing detached update signature as defense in depth.
5. Have counsel/accounting approve the EULA, privacy notice, refund policy, seller
   identity, VAT and merchant-of-record configuration.
