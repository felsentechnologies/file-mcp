#!/usr/bin/env bash
# Create a release-safe FileMCP source archive from tracked Git content only.
set -Eeuo pipefail
cd "$(dirname "$0")"

if [ -n "$(git status --porcelain)" ]; then
    echo "ERROR: working tree must be clean before creating a source archive." >&2
    exit 1
fi

if command -v plutil >/dev/null 2>&1; then
    VERSION="$(plutil -extract CFBundleShortVersionString raw -o - macos/Info.plist)"
else
    # Linux and other hosts without plutil: read the same key from the plist text.
    VERSION="$(sed -n '/<key>CFBundleShortVersionString<\/key>/{n;s:.*<string>\([^<]*\)</string>.*:\1:p;}' macos/Info.plist)"
fi
[ -n "$VERSION" ] || { echo "ERROR: could not read the version from macos/Info.plist." >&2; exit 1; }
PREFIX="filemcp-v${VERSION}/"
OUTPUT="dist/filemcp-v${VERSION}-source.zip"

mkdir -p dist
rm -f "$OUTPUT"
git archive --format=zip --prefix="$PREFIX" --output="$OUTPUT" HEAD

echo "Built: $OUTPUT"
if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$OUTPUT"; else sha256sum "$OUTPUT"; fi
