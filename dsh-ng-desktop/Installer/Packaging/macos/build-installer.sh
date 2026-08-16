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

project_root="$(cd "$(dirname "$0")/../../.." && pwd)"
project="$project_root/DshNgDesktop.csproj"
nuget_config="$project_root/NuGet.Config"
work="$(mktemp -d "${TMPDIR:-/tmp}/dsh-desktop-installer.XXXXXX")"
publish="$work/publish"
client_app="$work/DSH Desktop.app"
bootstrap_app="$work/DSH Desktop Installer.app"
pkg_root="$work/pkg-root"
package_scripts="$work/scripts"
pkg="$output/DSH-Desktop-Setup-$release_version-$rid-$package_flavor.pkg"
mkdir -p "$publish" "$output"
trap 'rm -rf "$work"' EXIT

echo "正在发布 $rid 的 $mode_description 客户端…"
dotnet restore "$project" -r "$rid" --configfile "$nuget_config"
dotnet publish "$project" -c Release -r "$rid" --no-restore \
  -p:DshPublishMode=Aot -p:Version="$version" -p:PublishSingleFile=true -p:SelfContained=true \
  -p:DebugType=None -p:DebugSymbols=false -o "$publish"
find "$publish" -name '*.dSYM' -type d -prune -exec rm -rf {} +

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
