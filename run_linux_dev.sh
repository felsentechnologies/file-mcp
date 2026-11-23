#!/usr/bin/env bash
# Builds the Linux app in Debug, places the vendored tunnel-client and ripgrep beside it and launches it.
# Extra arguments are passed to FileMCP (for example --hidden).
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

[[ "$(uname -s)" == "Linux" ]] || { echo "run_linux_dev.sh must be run on Linux." >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "dotnet 8 SDK is required." >&2; exit 1; }

case "$(uname -m)" in
    x86_64|amd64) VENDOR_TAG="linux-amd64" ;;
    aarch64|arm64) VENDOR_TAG="linux-arm64" ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac
TUNNEL_CLIENT="vendor/tunnel-client/$VENDOR_TAG/tunnel-client"
RIPGREP="vendor/ripgrep/$VENDOR_TAG/rg"
[[ -x "$TUNNEL_CLIENT" ]] || { echo "Missing tunnel-client for $VENDOR_TAG: $TUNNEL_CLIENT" >&2; exit 1; }
[[ -x "$RIPGREP" ]] || { echo "Missing ripgrep for $VENDOR_TAG: $RIPGREP" >&2; exit 1; }

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1
dotnet build linux/src/FileMCP.Linux/FileMCP.Linux.csproj -c Debug

OUTPUT="linux/src/FileMCP.Linux/bin/Debug/net8.0"
install -m 0755 "$TUNNEL_CLIENT" "$OUTPUT/tunnel-client"
install -m 0755 "$RIPGREP" "$OUTPUT/rg"
exec "$OUTPUT/FileMCP" "$@"
