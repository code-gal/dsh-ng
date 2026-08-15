#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "usage: $0 --rid osx-arm64|osx-x64 --version VERSION --signing-identity IDENTITY --notary-profile PROFILE [--mode Aot|Compatibility] [--output DIR]" >&2
  exit 2
}

mode=Aot
output="$(cd "$(dirname "$0")/../../.." && pwd)/artifacts/installer"
rid=
version=
identity=
notary_profile=
while (($#)); do
  case "$1" in
    --rid) rid=${2:-}; shift 2 ;;
    --version) version=${2:-}; shift 2 ;;
    --signing-identity) identity=${2:-}; shift 2 ;;
    --notary-profile) notary_profile=${2:-}; shift 2 ;;
    --mode) mode=${2:-}; shift 2 ;;
    --output) output=${2:-}; shift 2 ;;
    *) usage ;;
  esac
done
[[ "$rid" == osx-arm64 || "$rid" == osx-x64 ]] || usage
[[ -n "$version" && -n "$identity" && -n "$notary_profile" ]] || usage
[[ "$mode" == Aot || "$mode" == Compatibility ]] || usage

project_root="$(cd "$(dirname "$0")/../../.." && pwd)"
project="$project_root/DshNgDesktop.csproj"
work="$(mktemp -d "${TMPDIR:-/tmp}/dsh-desktop-installer.XXXXXX")"
publish="$work/publish"
client_app="$work/DSH Desktop.app"
bootstrap_app="$work/DSH Desktop Installer.app"
pkg_root="$work/pkg-root"
pkg="$output/DSH-Desktop-Setup-$version-$rid.pkg"
mkdir -p "$publish" "$output"
trap 'rm -rf "$work"' EXIT

dotnet publish "$project" -c Release -r "$rid" \
  -p:DshPublishMode="$mode" -p:PublishSingleFile=true -p:SelfContained=true -o "$publish"

make_app() {
  local app="$1"
  local bundle_identifier="$2"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/." "$app/Contents/MacOS/"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>DshNgDesktop</string>
  <key>CFBundleIdentifier</key><string>$bundle_identifier</string>
  <key>CFBundleName</key><string>DSH Desktop</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
</dict></plist>
PLIST
  cat > "$app/Contents/Resources/Uninstall DSH Desktop.command" <<'UNINSTALL'
#!/bin/sh
set -eu
"$(dirname "$0")/../MacOS/DshNgDesktop" --uninstall
UNINSTALL
  chmod 755 "$app/Contents/Resources/Uninstall DSH Desktop.command"
}

make_app "$client_app" "com.deepseekharness.dshdesktop"
make_app "$bootstrap_app" "com.deepseekharness.dshdesktop.installer"
mkdir -p "$bootstrap_app/Contents/Resources/payload"
cp -R "$client_app" "$bootstrap_app/Contents/Resources/payload/DSH Desktop.app"

# Sign the nested client before its enclosing bootstrap, then validate both
# bundles. The signing identity is supplied by the release machine keychain.
codesign --force --options runtime --timestamp --sign "$identity" "$client_app"
codesign --force --options runtime --timestamp --sign "$identity" "$bootstrap_app"
codesign --verify --deep --strict --verbose=2 "$client_app"
codesign --verify --deep --strict --verbose=2 "$bootstrap_app"

mkdir -p "$pkg_root/Library/Application Support/DSH Desktop Installer"
cp -R "$bootstrap_app" "$pkg_root/Library/Application Support/DSH Desktop Installer/"
pkgbuild --root "$pkg_root" \
  --identifier com.deepseekharness.dshdesktop.installer \
  --version "$version" \
  --scripts "$(dirname "$0")/scripts" \
  --install-location / \
  "$pkg"
productsign --sign "$identity" "$pkg" "$pkg.signed"
mv "$pkg.signed" "$pkg"
xcrun notarytool submit "$pkg" --keychain-profile "$notary_profile" --wait
xcrun stapler staple "$pkg"
xcrun stapler validate "$pkg"
shasum -a 256 "$pkg" > "$pkg.sha256"
