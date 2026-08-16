#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "用法：$0 --package PKG" >&2
  exit 2
}

package=
while (($#)); do
  case "$1" in
    --package) package=${2:-}; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$package" && -f "$package" ]] || usage
[[ "$(uname -s)" == "Darwin" ]] || { echo 'macOS ARM64 门禁必须在 Darwin 主机执行。' >&2; exit 1; }
[[ "$(uname -m)" == "arm64" ]] || { echo '无法执行 osx-arm64 AOT 运行门禁：当前宿主不是 arm64。' >&2; exit 1; }

script_directory="$(cd "$(dirname "$0")" && pwd)"
postinstall="$script_directory/scripts/postinstall"
bash -n "$postinstall"
! grep -q '/dev/null' "$postinstall"
grep -q 'transaction_status=' "$postinstall"
grep -q 'exit "\$transaction_status"' "$postinstall"
grep -q 'cleanup_installer_root' "$postinstall"
grep -q 'trap on_exit EXIT' "$postinstall"
grep -q 'HOME="\$home_directory"' "$postinstall"
grep -q 'TMPDIR="\$user_tmp_directory"' "$postinstall"
grep -q 'PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"' "$postinstall"

work="$(mktemp -d "${TMPDIR:-/tmp}/dsh-desktop-verify.XXXXXX")"
trap 'rm -rf "$work"' EXIT
expanded="$work/expanded"
pkgutil --expand-full "$package" "$expanded"

bootstrap_app="$(find "$expanded" -type d -name 'DSH Desktop Installer.app' -print -quit)"
[[ -n "$bootstrap_app" && -d "$bootstrap_app" ]] || { echo 'PKG 中找不到 bootstrap app。' >&2; exit 1; }
client_app="$bootstrap_app/Contents/Resources/payload/DSH Desktop.app"
[[ -d "$client_app" ]] || { echo 'PKG 中找不到客户端 app payload。' >&2; exit 1; }

verify_app() {
  local app="$1"
  local executable="$app/Contents/MacOS/DshNgDesktop"
  local helper="$app/Contents/MacOS/DshDesktop.WebKitCleanup"
  local uninstall_command="$app/Contents/Resources/Uninstall DSH Desktop.command"
  test -x "$executable"
  test -x "$helper"
  test -x "$uninstall_command"
  file -b "$executable" | grep -q 'arm64'
  file -b "$helper" | grep -q 'arm64'
  plutil -extract CFBundlePackageType raw -o - "$app/Contents/Info.plist" | grep -qx 'APPL'
  plutil -extract CFBundleIconFile raw -o - "$app/Contents/Info.plist" | grep -qx 'dsh.icns'
  plutil -extract LSMinimumSystemVersion raw -o - "$app/Contents/Info.plist" | grep -qx '14.0'
  vtool -show-build "$executable" | grep -Eq 'minos 14(\.0)?'
  vtool -show-build "$helper" | grep -Eq 'minos 14(\.0)?'
  test -f "$app/Contents/Resources/dsh.icns"
}

verify_app "$bootstrap_app"
verify_app "$client_app"

# Execute the actual arm64 Native AOT client in its read-only diagnostic role.
# The separate tracked ReleaseTests run in this same arm64 job cover the maintenance
# lease, mode-preserving deployment and spawn-time process-group path.
doctor_output="$work/doctor.json"
doctor_status=0
env -i HOME="$HOME" PATH="/usr/bin:/bin" "$client_app/Contents/MacOS/DshNgDesktop" --doctor >"$doctor_output" 2>&1 || doctor_status=$?
test -s "$doctor_output"
if [[ "$doctor_status" -gt 1 ]]; then
  echo "macOS arm64 AOT --doctor exited unexpectedly with code $doctor_status." >&2
  cat "$doctor_output" >&2
  exit 1
fi

echo 'macOS arm64 package structure, postinstall contract and Native AOT diagnostic execution passed.'
