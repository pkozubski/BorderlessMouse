#!/bin/bash
# Sprawdza dwie różne binarki tej samej aplikacji bez dotykania bazy uprawnień macOS.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${SIGN_IDENTITY:?Test wymaga certyfikatu wydania}"
: "${SIGN_KEYCHAIN:?Test wymaga tymczasowego pęku kluczy}"
TEST_DIR=$(mktemp -d)
trap 'rm -rf "$TEST_DIR"' EXIT
CERT="$PWD/BorderlessMouse/Resources/ReleaseSigning.cer"
CERT_HASH=$(/usr/bin/openssl x509 -inform DER -in "$CERT" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')

make_app() {
  local name="$1" version="$2" identifier="$3"
  local app="$TEST_DIR/$name.app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  printf 'int main(void) { return %s; }\n' "$version" > "$TEST_DIR/main.c"
  xcrun clang "$TEST_DIR/main.c" -o "$app/Contents/MacOS/Fixture"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$identifier</string>
<key>CFBundleExecutable</key><string>Fixture</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$version</string>
</dict></plist>
PLIST
  printf '%s\n' "$version" > "$app/Contents/Resources/version.txt"
  codesign --force --sign "$SIGN_IDENTITY" --keychain "$SIGN_KEYCHAIN" \
    --requirements "designated => identifier \"$identifier\" and anchor H\"$CERT_HASH\"" "$app"
}

make_app first 1 com.borderlessmouse.mac
make_app second 2 com.borderlessmouse.mac
make_app wrong-id 3 com.borderlessmouse.other
ditto "$TEST_DIR/second.app" "$TEST_DIR/adhoc.app"
codesign --force --sign - "$TEST_DIR/adhoc.app"
ditto "$TEST_DIR/second.app" "$TEST_DIR/unsigned.app"
codesign --remove-signature "$TEST_DIR/unsigned.app"
ditto "$TEST_DIR/second.app" "$TEST_DIR/tampered.app"
printf 'changed\n' > "$TEST_DIR/tampered.app/Contents/Resources/version.txt"
printf 'invalid certificate\n' > "$TEST_DIR/invalid.cer"

# Inny, poprawny certyfikat publiczny sprawdza odrzucenie niezgodnego wydawcy.
security find-certificate -c 'Apple Root CA' -p /System/Library/Keychains/SystemRootCertificates.keychain \
  | /usr/bin/openssl x509 -outform DER -out "$TEST_DIR/other.cer"
swiftc -parse-as-library -framework Security -framework CryptoKit \
  BorderlessMouse/Sources/App/ReleaseSignature.swift tests/ReleaseSignatureChecks.swift \
  -o "$TEST_DIR/checks"
"$TEST_DIR/checks" "$CERT" "$TEST_DIR" "$PWD/build/BorderlessMouse.app"
