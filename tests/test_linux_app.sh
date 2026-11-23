#!/usr/bin/env bash
# Linux counterpart of tests/test_windows_app.ps1: unpacks the release tarball, checks its contents and runs a
# GUI smoke test under Xvfb with an isolated D-Bus session and XDG directories:
#   - the app starts and owns its single-instance socket;
#   - a second launch hands over to the running instance and exits 0;
#   - SIGTERM shuts the app down cleanly and removes the socket.
#
#   ./tests/test_linux_app.sh [x64|arm64]     # default: host architecture
# Needs Xvfb (or XVFB=/path/to/Xvfb) and dbus-run-session for the GUI phase.
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

fail() { echo "::error file=tests/test_linux_app.sh,title=Linux app smoke failure::$*" >&2; exit 1; }

# Phase 2 runs inside dbus-run-session (see bottom of the file).
if [[ "${1:-}" == "--gui-phase" ]]; then
    APP="$2"
    "$APP/FileMCP" >"$SMOKE_ROOT/app.log" 2>&1 &
    APP_PID=$!
    SOCKET="$XDG_RUNTIME_DIR/filemcp/app.sock"
    for _ in $(seq 1 150); do
        [[ -S "$SOCKET" ]] && break
        kill -0 "$APP_PID" 2>/dev/null || { cat "$SMOKE_ROOT/app.log" >&2; fail "FileMCP exited during startup"; }
        sleep 0.2
    done
    [[ -S "$SOCKET" ]] || { cat "$SMOKE_ROOT/app.log" >&2; fail "FileMCP did not create its single-instance socket"; }
    sleep 2
    kill -0 "$APP_PID" 2>/dev/null || { cat "$SMOKE_ROOT/app.log" >&2; fail "FileMCP exited after startup"; }
    echo "linux-app-start: ok"

    timeout 20 "$APP/FileMCP" || fail "second launch did not hand over to the running instance"
    kill -0 "$APP_PID" 2>/dev/null || fail "first instance exited after the second launch"
    echo "linux-app-single-instance: ok"

    kill -TERM "$APP_PID"
    for _ in $(seq 1 100); do
        kill -0 "$APP_PID" 2>/dev/null || break
        sleep 0.1
    done
    if kill -0 "$APP_PID" 2>/dev/null; then kill -KILL "$APP_PID"; fail "FileMCP ignored SIGTERM"; fi
    wait "$APP_PID" 2>/dev/null || true
    [[ ! -e "$SOCKET" ]] || fail "single-instance socket was not removed on shutdown"
    echo "linux-app-sigterm-shutdown: ok"
    exit 0
fi

ARCH="${1:-}"
if [[ -z "$ARCH" ]]; then
    case "$(uname -m)" in x86_64|amd64) ARCH="x64" ;; aarch64|arm64) ARCH="arm64" ;; *) fail "unsupported host $(uname -m)" ;; esac
fi
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' linux/src/FileMCP.Linux/FileMCP.Linux.csproj | head -n 1)"
ARCHIVE="$ROOT/dist/FileMCP-v$VERSION-linux-$ARCH.tar.gz"
[[ -f "$ARCHIVE" ]] || fail "Linux $ARCH release archive is missing: $ARCHIVE (run ./build_linux_app.sh --arch $ARCH)"

SMOKE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/filemcp-linux-app-smoke-XXXXXXXX")"
XVFB_PID=""
cleanup() {
    [[ -n "$XVFB_PID" ]] && kill "$XVFB_PID" 2>/dev/null || true
    rm -rf "$SMOKE_ROOT"
}
trap cleanup EXIT

tar -xzf "$ARCHIVE" -C "$SMOKE_ROOT"
APP="$SMOKE_ROOT/FileMCP"
EXPECTED_FILES="FileMCP
FileMCP-LICENSE.txt
libHarfBuzzSharp.so
libSkiaSharp.so
rg
ripgrep-COPYING.txt
ripgrep-LICENSE-MIT.txt
ripgrep-UNLICENSE.txt
tunnel-client
tunnel-client-LICENSE.txt
tunnel-client-NOTICE.txt
tunnel-client-THIRD-PARTY-LICENSES.txt"
ACTUAL_FILES="$(cd "$APP" && find . -mindepth 1 | sed 's:^\./::' | LC_ALL=C sort)"
if [[ "$ACTUAL_FILES" != "$EXPECTED_FILES" ]]; then
    diff <(echo "$EXPECTED_FILES") <(echo "$ACTUAL_FILES") >&2 || true
    fail "unexpected file list in the Linux $ARCH release archive"
fi
for executable in FileMCP tunnel-client rg; do
    [[ -x "$APP/$executable" ]] || fail "$executable is not executable"
done
echo "linux-release-layout: ok"

HOST_ARCH="$(uname -m)"
if [[ ("$ARCH" == "x64" && "$HOST_ARCH" == "x86_64") || ("$ARCH" == "arm64" && "$HOST_ARCH" == "aarch64") ]]; then
    "$APP/FileMCP" --version | grep -qx "FileMCP $VERSION (linux-$ARCH)" || fail "unexpected FileMCP --version output"
    "$APP/rg" --version | head -n 1 | grep -q '^ripgrep 15\.2\.0 ' || fail "unexpected bundled ripgrep version"
    "$APP/tunnel-client" --version | grep -q '^0\.0\.12+881c9a8fed7cccbe6607cd419863bbca506b8215 ' || fail "unexpected bundled tunnel-client version"
    echo "linux-release-binaries: ok"
else
    echo "linux-release-binaries: skipped (cross-architecture archive)"
    exit 0
fi

XVFB="${XVFB:-$(command -v Xvfb || true)}"
if [[ -z "$XVFB" ]] || ! command -v dbus-run-session >/dev/null 2>&1; then
    echo "linux-app-gui: skipped (Xvfb or dbus-run-session not installed)"
    exit 0
fi

DISPLAY_NUMBER=$(( 90 + RANDOM % 100 ))
"$XVFB" ":$DISPLAY_NUMBER" -screen 0 1280x800x24 -nolisten tcp >"$SMOKE_ROOT/xvfb.log" 2>&1 &
XVFB_PID=$!
for _ in $(seq 1 50); do
    [[ -S "/tmp/.X11-unix/X$DISPLAY_NUMBER" ]] && break
    sleep 0.1
done
[[ -S "/tmp/.X11-unix/X$DISPLAY_NUMBER" ]] || { cat "$SMOKE_ROOT/xvfb.log" >&2; fail "Xvfb did not start"; }

mkdir -p "$SMOKE_ROOT/runtime" "$SMOKE_ROOT/config" "$SMOKE_ROOT/data" "$SMOKE_ROOT/home"
chmod 0700 "$SMOKE_ROOT/runtime"
env DISPLAY=":$DISPLAY_NUMBER" HOME="$SMOKE_ROOT/home" XDG_RUNTIME_DIR="$SMOKE_ROOT/runtime" \
    XDG_CONFIG_HOME="$SMOKE_ROOT/config" XDG_DATA_HOME="$SMOKE_ROOT/data" SMOKE_ROOT="$SMOKE_ROOT" \
    dbus-run-session -- "$0" --gui-phase "$APP"
