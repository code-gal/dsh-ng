#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "用法：$0 --version 版本号 [--output 输出目录]" >&2
  exit 2
}

output="$(cd "$(dirname "$0")/../../.." && pwd)/artifacts/installer"
version=
while (($#)); do
  case "$1" in
    --version) version=${2:-}; shift 2 ;;
    --output) output=${2:-}; shift 2 ;;
    *) usage ;;
  esac
done
[[ -n "$version" ]] || usage
version="${version#v}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] || {
  echo '版本号必须是 0.9.1 或 v0.9.1 形式的 SemVer。' >&2
  exit 2
}
release_version="v$version"
rid=osx-arm64
package_flavor=aot
mode_description='Native AOT（自包含，无需 .NET Runtime）'
macos_minimum_version=14.0

project_root="$(cd "$(dirname "$0")/../../.." && pwd)"
project="$project_root/DshNgDesktop.csproj"
nuget_config="$project_root/NuGet.Config"
webkit_helper_source="$project_root/Installer/Packaging/macos/WebKitCleanup.m"
icon_source="$project_root/Assets/dsh-ng-desktop-start-white.png"
work="$(mktemp -d "${TMPDIR:-/tmp}/dsh-desktop-installer.XXXXXX")"
publish="$work/publish"
client_app="$work/DSH Desktop.app"
bootstrap_app="$work/DSH Desktop Installer.app"
pkg_root="$work/pkg-root"
package_scripts="$work/scripts"
iconset="$work/dsh.iconset"
dsym_output="$output/.internal/dsym/$version"
pkg="$output/DSH-Desktop-Setup-$release_version-$rid-$package_flavor.pkg"
mkdir -p "$publish" "$output" "$dsym_output" "$iconset"
trap 'rm -rf "$work"' EXIT

echo "正在发布 $rid 的 $mode_description 客户端…"
export MACOSX_DEPLOYMENT_TARGET="$macos_minimum_version"
dotnet restore "$project" -r "$rid" --configfile "$nuget_config"
dotnet publish "$project" -c Release -r "$rid" --no-restore \
  -p:DshPublishMode=Aot -p:Version="$version" -p:PublishSingleFile=true -p:SelfContained=true \
  -p:DebugType=Full -p:DebugSymbols=true -o "$publish"

dsym_count=0
while IFS= read -r dsym; do
  dsym_count=$((dsym_count + 1))
  cp -R "$dsym" "$dsym_output/"
done < <(find "$work" -name '*.dSYM' -type d -print)
if [[ "$dsym_count" -ne 1 ]]; then
  echo "必须从本次 Native AOT 构建保存且仅保存一份匹配的 dSYM，实际找到 $dsym_count 份。" >&2
  exit 1
fi
find "$publish" -name '*.dSYM' -type d -prune -exec rm -rf {} +
find "$publish" -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete

clang -fobjc-arc -arch arm64 -mmacosx-version-min="$macos_minimum_version" -framework Foundation -framework WebKit \
  "$webkit_helper_source" -o "$publish/DshDesktop.WebKitCleanup"
chmod 755 "$publish/DshDesktop.WebKitCleanup"

for size in 16 32 64 128 256 512 1024; do
  sips -z "$size" "$size" "$icon_source" --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
cp "$iconset/icon_32x32.png" "$iconset/icon_16x16@2x.png"
cp "$iconset/icon_64x64.png" "$iconset/icon_32x32@2x.png"
cp "$iconset/icon_256x256.png" "$iconset/icon_128x128@2x.png"
cp "$iconset/icon_512x512.png" "$iconset/icon_256x256@2x.png"
cp "$iconset/icon_1024x1024.png" "$iconset/icon_512x512@2x.png"
rm "$iconset/icon_64x64.png" "$iconset/icon_1024x1024.png"
iconutil -c icns "$iconset" -o "$work/dsh.icns"

make_app() {
  local app="$1"
  local bundle_identifier="$2"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/." "$app/Contents/MacOS/"
  cp "$work/dsh.icns" "$app/Contents/Resources/dsh.icns"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>DshNgDesktop</string>
  <key>CFBundleIdentifier</key><string>$bundle_identifier</string>
  <key>CFBundleName</key><string>DSH Desktop</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleIconFile</key><string>dsh.icns</string>
  <key>CFBundleIconName</key><string>dsh</string>
  <key>LSMinimumSystemVersion</key><string>$macos_minimum_version</string>
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

verify_app() {
  local app="$1"
  local executable="$app/Contents/MacOS/DshNgDesktop"
  local webkit_helper="$app/Contents/MacOS/DshDesktop.WebKitCleanup"
  local uninstall_command="$app/Contents/Resources/Uninstall DSH Desktop.command"
  test -x "$executable"
  test -x "$webkit_helper"
  test -x "$uninstall_command"
  file -b "$executable" | grep -q 'arm64'
  file -b "$webkit_helper" | grep -q 'arm64'
  test -f "$app/Contents/Resources/dsh.icns"
  plutil -extract CFBundlePackageType raw -o - "$app/Contents/Info.plist" | grep -qx 'APPL'
  plutil -extract CFBundleIconFile raw -o - "$app/Contents/Info.plist" | grep -qx 'dsh.icns'
  plutil -extract LSMinimumSystemVersion raw -o - "$app/Contents/Info.plist" | grep -qx "$macos_minimum_version"
  vtool -show-build "$executable" | grep -Eq 'minos 14(\.0)?'
  vtool -show-build "$webkit_helper" | grep -Eq 'minos 14(\.0)?'
  while IFS= read -r dependency; do
    case "$dependency" in
      @rpath/*|@loader_path/*)
        dependency_name="${dependency##*/}"
        test -f "$app/Contents/MacOS/$dependency_name"
        ;;
    esac
  done < <(otool -L "$executable" | sed '1d' | sed 's/^[[:space:]]*//' | sed 's/ (.*$//')
}

verify_app "$client_app"
verify_app "$bootstrap_app"
mkdir -p "$bootstrap_app/Contents/Resources/payload"
cp -R "$client_app" "$bootstrap_app/Contents/Resources/payload/DSH Desktop.app"

mkdir -p "$package_scripts"
sed "s/@PACKAGE_VERSION@/$version/g" "$(dirname "$0")/scripts/postinstall" > "$package_scripts/postinstall"
chmod 755 "$package_scripts/postinstall"

mkdir -p "$pkg_root/Library/Application Support/DSH Desktop Installer"
cp -R "$bootstrap_app" "$pkg_root/Library/Application Support/DSH Desktop Installer/"
pkgbuild --root "$pkg_root" \
  --identifier com.deepseekharness.dshdesktop.installer \
  --version "$version" \
  --scripts "$package_scripts" \
  --install-location / \
  "$pkg"
(
  cd "$(dirname "$pkg")"
  shasum -a 256 "$(basename "$pkg")" > "$(basename "$pkg").sha256"
)
echo "已生成安装包：$pkg"
echo "已生成 SHA-256：$pkg.sha256"
