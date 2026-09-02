#!/bin/bash
# Tylko dla izolowanego runnera GitHub Actions. Klucz nigdy nie trafia do repozytorium ani artefaktów.
set -euo pipefail
[ "${GITHUB_ACTIONS:-}" = "true" ] || { echo "Ten skrypt jest przeznaczony dla GitHub Actions." >&2; exit 1; }
[ "${RUNNER_ENVIRONMENT:-}" = "github-hosted" ] || { echo "Podpisywanie wymaga jednorazowego runnera GitHub-hosted." >&2; exit 1; }
: "${MACOS_SIGNING_P12_BASE64:?Brak certyfikatu w GitHub Secrets}"
: "${MACOS_SIGNING_P12_PASSWORD:?Brak hasła certyfikatu w GitHub Secrets}"
: "${RUNNER_TEMP:?}"
: "${GITHUB_ENV:?}"
umask 077

SIGNING_ARCHIVE="$RUNNER_TEMP/borderlessmouse-signing.p12"
SIGNING_KEYCHAIN="$RUNNER_TEMP/borderlessmouse-signing.keychain-db"
SIGNING_CERT="$GITHUB_WORKSPACE/macos/BorderlessMouse/Resources/ReleaseSigning.cer"
SIGNING_KEYCHAIN_PASSWORD=$(/usr/bin/openssl rand -hex 32)
echo "::add-mask::$SIGNING_KEYCHAIN_PASSWORD"
trap 'rm -f "$SIGNING_ARCHIVE"' EXIT
printf '%s' "$MACOS_SIGNING_P12_BASE64" | base64 --decode > "$SIGNING_ARCHIVE"

security create-keychain -p "$SIGNING_KEYCHAIN_PASSWORD" "$SIGNING_KEYCHAIN"
security set-keychain-settings -lut 21600 "$SIGNING_KEYCHAIN"
security unlock-keychain -p "$SIGNING_KEYCHAIN_PASSWORD" "$SIGNING_KEYCHAIN"
security import "$SIGNING_ARCHIVE" -k "$SIGNING_KEYCHAIN" -P "$MACOS_SIGNING_P12_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$SIGNING_KEYCHAIN_PASSWORD" "$SIGNING_KEYCHAIN" > /dev/null
# Zaufanie tylko do podpisywania kodu, tylko na jednorazowym runnerze. Nie zmieniamy ustawień użytkowników aplikacji.
sudo security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain "$SIGNING_CERT"
security list-keychains -d user -s "$SIGNING_KEYCHAIN"

SIGNING_FINGERPRINT=$(/usr/bin/openssl x509 -inform DER -in "$SIGNING_CERT" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')
security verify-cert -c "$SIGNING_CERT" -p codeSign
security find-identity -p codesigning "$SIGNING_KEYCHAIN"
security find-identity -v -p codesigning "$SIGNING_KEYCHAIN" | grep -F "$SIGNING_FINGERPRINT"
{
  echo "SIGN_IDENTITY=$SIGNING_FINGERPRINT"
  echo "SIGN_KEYCHAIN=$SIGNING_KEYCHAIN"
  echo "REQUIRE_STABLE_SIGNING=1"
} >> "$GITHUB_ENV"
