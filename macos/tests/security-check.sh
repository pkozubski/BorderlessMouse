#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
TEST_DIR=$(mktemp -d)
trap 'rm -rf "$TEST_DIR"' EXIT
swiftc -parse-as-library -swift-version 5 -framework CryptoKit -framework Security \
  macos/BorderlessMouse/Sources/Security/PairingSecurity.swift \
  macos/BorderlessMouse/Sources/Security/ArtifactSignature.swift \
  macos/BorderlessMouse/Sources/Localization/L10n.swift \
  macos/BorderlessMouse/Sources/Protocol/Protocol.swift \
  macos/BorderlessMouse/Sources/Protocol/ClipboardContent.swift \
  macos/tests/SecurityChecks.swift -o "$TEST_DIR/security-checks"
"$TEST_DIR/security-checks"
