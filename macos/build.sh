#!/bin/bash
# Buduje BorderlessMouse.app bez Xcode (wystarczą Command Line Tools).
#   ./build.sh                       → build/BorderlessMouse.app (release, arch hosta)
#   CONFIG=debug ./build.sh          → bez optymalizacji
#   ARCH=x86_64 ./build.sh           → inna architektura
#   UNIVERSAL=1 ./build.sh           → arm64 + x86_64 (lipo)
#   VERSION=1.2.3 BUILD_NUMBER=42 ./build.sh
#   SIGN_IDENTITY="BorderlessMouse Dev" ./build.sh   → stały certyfikat (trwałe uprawnienia TCC)
set -euo pipefail
cd "$(dirname "$0")"

ARCH="${ARCH:-$(uname -m)}"
CONFIG="${CONFIG:-release}"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
if [ "${REQUIRE_STABLE_SIGNING:-0}" = "1" ] && [ "$SIGN_IDENTITY" = "-" ]; then
  echo "✗ Wydanie wymaga stałego certyfikatu; podpis ad-hoc zerwałby uprawnienia macOS." >&2
  exit 1
fi
VERSION="${VERSION:-1.0.0}"
BUILD_NUMBER="${BUILD_NUMBER:-1}"
DEPLOY="14.2"
BUNDLE_ID="com.borderlessmouse.mac"
OUT="build/BorderlessMouse.app"

# SDK decyduje o wyglądzie: binarka zlinkowana z SDK macOS 26 dostaje na Tahoe nowy
# wygląd systemu (szklane kontrolki), starsze SDK dają tryb kompatybilności.
# Dlatego wybieramy najnowszy SDK spośród Command Line Tools i wszystkich Xcode.
pick_sdk() {
  local best="" best_ver="0"
  for sdk in /Library/Developer/CommandLineTools/SDKs/MacOSX[0-9]*.sdk \
             /Applications/Xcode*.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX[0-9]*.sdk; do
    [ -d "$sdk" ] || continue
    local ver
    ver="$(basename "$sdk" | sed -E 's/MacOSX([0-9.]+)\.sdk/\1/')"
    if [ "$(printf '%s\n%s\n' "$best_ver" "$ver" | sort -V | tail -1)" = "$ver" ] && [ "$ver" != "$best_ver" ]; then
      best="$sdk"; best_ver="$ver"
    fi
  done
  if [ -z "$best" ]; then best="$(xcrun --sdk macosx --show-sdk-path)"; best_ver="$(xcrun --sdk macosx --show-sdk-version)"; fi
  SDK="$best"; SDK_VERSION="$best_ver"
}
pick_sdk
echo "→ SDK macOS $SDK_VERSION ($SDK)"
if [ -n "${REQUIRE_SDK_MAJOR:-}" ] && [ "${SDK_VERSION%%.*}" -lt "$REQUIRE_SDK_MAJOR" ]; then
  echo "✗ Wymagany SDK macOS ${REQUIRE_SDK_MAJOR}+, znaleziono ${SDK_VERSION}. Zainstaluj nowsze Xcode/Command Line Tools." >&2
  exit 1
fi

OPT="-O"
[ "$CONFIG" = "debug" ] && OPT="-Onone -g"

rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources" build/obj

SOURCES=$(find BorderlessMouse/Sources -name '*.swift' | sort)

compile() { # $1 = arch, $2 = output
  echo "→ swiftc ($CONFIG, $1)"
  swiftc $OPT -swift-version 5 -parse-as-library -module-name BorderlessMouse \
    -target "$1-apple-macos${DEPLOY}" -sdk "$SDK" \
    -framework SwiftUI -framework AppKit -framework Network \
    -framework CoreAudio -framework AudioToolbox -framework CryptoKit \
    -framework ServiceManagement -framework Security \
    -o "$2" $SOURCES
}

if [ "${UNIVERSAL:-0}" = "1" ]; then
  compile arm64 build/obj/BorderlessMouse-arm64
  compile x86_64 build/obj/BorderlessMouse-x86_64
  lipo -create build/obj/BorderlessMouse-arm64 build/obj/BorderlessMouse-x86_64 \
       -output "$OUT/Contents/MacOS/BorderlessMouse"
else
  compile "$ARCH" "$OUT/Contents/MacOS/BorderlessMouse"
fi

sed -e "s/\$(PRODUCT_BUNDLE_IDENTIFIER)/$BUNDLE_ID/g" \
    -e 's/\$(EXECUTABLE_NAME)/BorderlessMouse/g' \
    -e 's/\$(PRODUCT_NAME)/BorderlessMouse/g' \
    -e "s/\$(MARKETING_VERSION)/$VERSION/g" \
    -e "s/\$(CURRENT_PROJECT_VERSION)/$BUILD_NUMBER/g" \
    -e "s/\$(MACOSX_DEPLOYMENT_TARGET)/$DEPLOY/g" \
    BorderlessMouse/Info.plist > "$OUT/Contents/Info.plist"
printf 'APPL????' > "$OUT/Contents/PkgInfo"
cp BorderlessMouse/Resources/AppIcon.icns BorderlessMouse/Resources/BorderlessMouse.entitlements "$OUT/Contents/Resources/"
cp BorderlessMouse/Resources/ReleaseSigning.cer "$OUT/Contents/Resources/"
cp ../THIRD_PARTY_NOTICES.md "$OUT/Contents/Resources/THIRD_PARTY_NOTICES.txt"
for localization in BorderlessMouse/Resources/*.lproj; do
  [ -d "$localization" ] && cp -R "$localization" "$OUT/Contents/Resources/"
done

echo "→ codesign ($SIGN_IDENTITY)"
SIGN_ARGS=(--force --sign "$SIGN_IDENTITY" --options runtime --entitlements BorderlessMouse/BorderlessMouse.entitlements)
if [ -n "${SIGN_KEYCHAIN:-}" ]; then SIGN_ARGS+=(--keychain "$SIGN_KEYCHAIN"); fi
if [ "${REQUIRE_STABLE_SIGNING:-0}" = "1" ]; then
  CERT_HASH=$(/usr/bin/openssl x509 -inform DER -in BorderlessMouse/Resources/ReleaseSigning.cer -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')
  SIGN_ARGS+=(--requirements "=designated => identifier \"$BUNDLE_ID\" and anchor H\"$CERT_HASH\"")
  SIGN_ARGS+=(--timestamp)
fi
codesign "${SIGN_ARGS[@]}" "$OUT"
codesign --verify --strict --deep --all-architectures "$OUT"
if [ "${REQUIRE_STABLE_SIGNING:-0}" = "1" ]; then
  codesign --verify --strict --all-architectures \
    -R "=identifier \"$BUNDLE_ID\" and anchor H\"$CERT_HASH\"" "$OUT"
fi
echo "✓ $OUT (v$VERSION build $BUILD_NUMBER, SDK $SDK_VERSION)"
