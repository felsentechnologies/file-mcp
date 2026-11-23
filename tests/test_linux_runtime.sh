#!/usr/bin/env bash
# Linux counterpart of tests/test_windows_runtime.ps1: checks that the vendored tunnel-client does not persist
# the per-runtime local auth credential, then runs the shared FileMCP.Core integration suite on Linux.
#
# The credential suite uses a real Secret Service when one is reachable. For a headless run (CI), start one with:
#   dbus-run-session -- bash -c 'printf pass | gnome-keyring-daemon --unlock --components=secrets >/dev/null;
#                                FILEMCP_TEST_SECRET_SERVICE=real ./tests/test_linux_runtime.sh'
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

[[ "$(uname -s)" == "Linux" ]] || { echo "Linux integration tests must run on Linux." >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "dotnet 8 SDK is required." >&2; exit 1; }
command -v git >/dev/null 2>&1 || { echo "git is required." >&2; exit 1; }

case "$(uname -m)" in
    x86_64|amd64) VENDOR_TAG="linux-amd64" ;;
    aarch64|arm64) VENDOR_TAG="linux-arm64" ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac
TUNNEL_CLIENT="$ROOT/vendor/tunnel-client/$VENDOR_TAG/tunnel-client"
RIPGREP="$ROOT/vendor/ripgrep/$VENDOR_TAG/rg"
[[ -x "$TUNNEL_CLIENT" ]] || { echo "Missing vendored Linux tunnel-client: $TUNNEL_CLIENT" >&2; exit 1; }
[[ -x "$RIPGREP" ]] || { echo "Missing vendored Linux ripgrep: $RIPGREP" >&2; exit 1; }

PROFILE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/filemcp-linux-tunnel-profile-XXXXXXXX")"
trap 'rm -rf "$PROFILE_ROOT"' EXIT
PROFILE_NAME="local-auth-profile"
LOCAL_AUTH_TOKEN="$(printf 'a%.0s' {1..64})"

FILEMCP_LOCAL_AUTH_TOKEN="$LOCAL_AUTH_TOKEN" \
MCP_EXTRA_HEADERS="X-FileMCP-Local-Token: env:FILEMCP_LOCAL_AUTH_TOKEN" \
MCP_DISCOVERY_EXTRA_HEADERS="X-FileMCP-Local-Token: env:FILEMCP_LOCAL_AUTH_TOKEN" \
"$TUNNEL_CLIENT" init \
    --sample sample_mcp_remote_no_auth \
    --profile "$PROFILE_NAME" \
    --profile-dir "$PROFILE_ROOT" \
    --force \
    --tunnel-id tunnel_0123456789abcdef0123456789abcdef \
    --mcp-server-url http://127.0.0.1:18088/mcp \
    --health-listen-addr 127.0.0.1:0 >/dev/null
PROFILE_PATH="$PROFILE_ROOT/$PROFILE_NAME.yaml"
[[ -f "$PROFILE_PATH" ]] || { echo "Vendored tunnel-client did not create the expected profile." >&2; exit 1; }
if grep -q "$LOCAL_AUTH_TOKEN" "$PROFILE_PATH" || grep -Eiq 'extra_headers|X-FileMCP-Local-Token|FILEMCP_LOCAL_AUTH_TOKEN' "$PROFILE_PATH"; then
    echo "Vendored tunnel-client persisted the per-runtime local auth credential or header configuration." >&2
    exit 1
fi
echo "linux-tunnel-client-local-auth-env: ok"

export FILEMCP_RG="$RIPGREP" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet run --project windows/tests/FileMCP.Core.Tests/FileMCP.Core.Tests.csproj -c Release
