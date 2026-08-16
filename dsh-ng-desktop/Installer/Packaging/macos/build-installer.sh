#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "用法：$0 --rid osx-arm64|osx-x64 --version 版本号 --signing-identity 签名身份 --notary-profile 公证配置 [--mode Aot|DotNet] [--output 输出目录]" >&2
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
[[ "$mode" == Aot || "$mode" == DotNet ]] || usage
version="${version#v}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] || {
  echo '版本号必须是 0.9.1 或 v0.9.1 形式的 SemVer。' >&2
  exit 2
}
release_version="v$version"
package_flavor=aot
self_contained=true
mode_description='Native AOT（自包含，无需 .NET Runtime）'
if [[ "$mode" == DotNet ]]; then
  package_flavor=dotnet
  self_contained=false
  mode_description='.NET 依赖（需要预先安装 .NET Runtime）'
fi

project_root="$(cd "$(dirname "$0")/../../.." && pwd)"
project="$project_root/DshNgDesktop.csproj"
work="$(mktemp -d "${TMPDIR:-/tmp}/dsh-desktop-installer.XXXXXX")"
publish="$work/publish"
client_app="$work/DSH Desktop.app"
bootstrap_app="$work/DSH Desktop Installer.app"
pkg_root="$work/pkg-root"
pkg="$output/DSH-Desktop-Setup-$release_version-$rid-$package_flavor.pkg"
mkdir -p "$publish" "$output"
trap 'rm -rf "$work"' EXIT

echo "正在发布 $rid 的 $mode_description 客户端…"
dotnet publish "$project" -c Release -r "$rid" \
  -p:DshPublishMode="$mode" -p:PublishSingleFile=true -p:SelfContained="$self_contained" -o "$publish"

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
echo "已生成安装包：$pkg"
echo "已生成 SHA-256：$pkg.sha256"
