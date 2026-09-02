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
VERSION="${VERSION:-1.0.0}"
BUILD_NUMBER="${BUILD_NUMBER:-1}"
DEPLOY="14.2"
BUNDLE_ID="com.borderlessmouse.mac"
OUT="build/BorderlessMouse.app"
SDK="$(xcrun --sdk macosx --show-sdk-path)"

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
    -framework ServiceManagement \
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

echo "→ codesign ($SIGN_IDENTITY)"
codesign --force --sign "$SIGN_IDENTITY" \
  --entitlements BorderlessMouse/BorderlessMouse.entitlements \
  "$OUT"
echo "✓ $OUT (v$VERSION build $BUILD_NUMBER)"
