#!/usr/bin/env bash
# Builds the native Linux FileMCP client (Avalonia + FileMCP.Core) and packages it as a portable
# tar.gz, a Debian/Ubuntu .deb and a Fedora/RHEL/openSUSE .rpm.
#
#   ./build_linux_app.sh                         # x64, every format whose tooling is installed
#   ./build_linux_app.sh --arch arm64            # cross-build for aarch64
#   ./build_linux_app.sh --formats deb,rpm       # explicit formats fail if tooling is missing
#
# Requirements: .NET 8 SDK; dpkg-deb for .deb; rpmbuild (Debian/Ubuntu: `rpm`, Fedora: `rpm-build`) for .rpm.
# FILEMCP_PACKAGE_MAINTAINER overrides the package maintainer/packager field.
set -Eeuo pipefail

usage() {
    sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
}

die() { echo "build_linux_app.sh: $*" >&2; exit 1; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

ARCH="x64"
FORMATS=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        -a|--arch) [[ $# -ge 2 ]] || die "--arch needs a value"; ARCH="$2"; shift 2 ;;
        -f|--formats) [[ $# -ge 2 ]] || die "--formats needs a value"; FORMATS="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument: $1 (see --help)" ;;
    esac
done

case "$ARCH" in
    x64|amd64|x86_64) ARCH="x64"; RID="linux-x64"; VENDOR_TAG="linux-amd64"; DEB_ARCH="amd64"; RPM_ARCH="x86_64" ;;
    arm64|aarch64) ARCH="arm64"; RID="linux-arm64"; VENDOR_TAG="linux-arm64"; DEB_ARCH="arm64"; RPM_ARCH="aarch64" ;;
    *) die "unsupported architecture '$ARCH' (use x64 or arm64)" ;;
esac

EXPLICIT_FORMATS=1
if [[ -z "$FORMATS" ]]; then
    EXPLICIT_FORMATS=0
    FORMATS="tar"
    command -v dpkg-deb >/dev/null 2>&1 && FORMATS+=",deb"
    command -v rpmbuild >/dev/null 2>&1 && FORMATS+=",rpm"
fi
want() { [[ ",$FORMATS," == *",$1,"* ]]; }
for format in ${FORMATS//,/ }; do
    case "$format" in tar|deb|rpm) ;; *) die "unknown format '$format' (use tar, deb, rpm)" ;; esac
done
if want deb; then command -v dpkg-deb >/dev/null 2>&1 || die "dpkg-deb is required for .deb packages (install dpkg)"; fi
if want rpm; then command -v rpmbuild >/dev/null 2>&1 || die "rpmbuild is required for .rpm packages (Debian/Ubuntu: apt install rpm; Fedora: dnf install rpm-build)"; fi
command -v dotnet >/dev/null 2>&1 || die "the .NET 8 SDK (dotnet) is required"

PROJECT="linux/src/FileMCP.Linux/FileMCP.Linux.csproj"
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "could not read a semantic version from $PROJECT"
PACKAGE="filemcp"
APP_ID="com.localfilesmcp.FileMCP"
HOMEPAGE="https://github.com/felsentechnologies/file-mcp"
MAINTAINER="${FILEMCP_PACKAGE_MAINTAINER:-FileMCP contributors <filemcp@users.noreply.github.com>}"
SUMMARY="Share a local workspace with ChatGPT through MCP"
INSTALL_DIR="/usr/lib/filemcp"

TUNNEL_CLIENT="vendor/tunnel-client/$VENDOR_TAG/tunnel-client"
TUNNEL_THIRD_PARTY="vendor/tunnel-client/$VENDOR_TAG/THIRD-PARTY-LICENSES.txt"
RIPGREP="vendor/ripgrep/$VENDOR_TAG/rg"
ICON_DIR="assets/branding/filemcp-v2"
for required in \
    "$TUNNEL_CLIENT" "$TUNNEL_THIRD_PARTY" LICENSE vendor/tunnel-client/LICENSE vendor/tunnel-client/NOTICE \
    "$RIPGREP" vendor/ripgrep/COPYING vendor/ripgrep/LICENSE-MIT vendor/ripgrep/UNLICENSE \
    "$ICON_DIR/app-icon.svg" "$ICON_DIR/app-icon-256.png" \
    "linux/packaging/$APP_ID.desktop" "linux/packaging/$APP_ID.metainfo.xml" linux/packaging/debian/copyright; do
    [[ -f "$required" ]] || die "missing required file: $required"
done

# Reproducible timestamps and no build telemetry.
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct 2>/dev/null || date +%s)}"
export SOURCE_DATE_EPOCH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

BUILD_DIR="$ROOT/build/linux-$ARCH"
DIST_DIR="$ROOT/dist"
APP_DIR="$DIST_DIR/linux-$ARCH/FileMCP"
rm -rf "$BUILD_DIR" "$DIST_DIR/linux-$ARCH"
mkdir -p "$BUILD_DIR" "$APP_DIR"

echo "==> Publishing FileMCP $VERSION for $RID"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=false \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$APP_DIR"
[[ -x "$APP_DIR/FileMCP" ]] || die "publish did not produce $APP_DIR/FileMCP"

# Same side-by-side layout and legal file names as the Windows zip and the macOS bundle.
install -m 0755 "$TUNNEL_CLIENT" "$APP_DIR/tunnel-client"
install -m 0755 "$RIPGREP" "$APP_DIR/rg"
install -m 0644 LICENSE "$APP_DIR/FileMCP-LICENSE.txt"
install -m 0644 vendor/tunnel-client/LICENSE "$APP_DIR/tunnel-client-LICENSE.txt"
install -m 0644 vendor/tunnel-client/NOTICE "$APP_DIR/tunnel-client-NOTICE.txt"
install -m 0644 "$TUNNEL_THIRD_PARTY" "$APP_DIR/tunnel-client-THIRD-PARTY-LICENSES.txt"
install -m 0644 vendor/ripgrep/COPYING "$APP_DIR/ripgrep-COPYING.txt"
install -m 0644 vendor/ripgrep/LICENSE-MIT "$APP_DIR/ripgrep-LICENSE-MIT.txt"
install -m 0644 vendor/ripgrep/UNLICENSE "$APP_DIR/ripgrep-UNLICENSE.txt"
find "$APP_DIR" -type f -name '*.so' -exec chmod 0644 {} +
chmod 0755 "$APP_DIR/FileMCP"

ARTIFACTS=()

if want tar; then
    TARBALL="$DIST_DIR/FileMCP-v$VERSION-linux-$ARCH.tar.gz"
    echo "==> Creating $(basename "$TARBALL")"
    tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$SOURCE_DATE_EPOCH" \
        -C "$DIST_DIR/linux-$ARCH" -cf - FileMCP | gzip -n -9 > "$TARBALL"
    ARTIFACTS+=("$TARBALL")
fi

# Filesystem payload shared by .deb and .rpm (FHS layout).
PAYLOAD="$BUILD_DIR/payload"
stage_payload() {
    rm -rf "$PAYLOAD"
    install -d -m 0755 "$PAYLOAD$INSTALL_DIR" "$PAYLOAD/usr/bin" "$PAYLOAD/usr/share/applications" "$PAYLOAD/usr/share/metainfo"
    cp -a "$APP_DIR/." "$PAYLOAD$INSTALL_DIR/"
    ln -s "../lib/filemcp/FileMCP" "$PAYLOAD/usr/bin/filemcp"
    install -m 0644 "linux/packaging/$APP_ID.desktop" "$PAYLOAD/usr/share/applications/$APP_ID.desktop"
    install -m 0644 "linux/packaging/$APP_ID.metainfo.xml" "$PAYLOAD/usr/share/metainfo/$APP_ID.metainfo.xml"
    for size in 16 24 32 48 64 128 256 512; do
        install -D -m 0644 "$ICON_DIR/app-icon-$size.png" "$PAYLOAD/usr/share/icons/hicolor/${size}x${size}/apps/filemcp.png"
    done
    install -D -m 0644 "$ICON_DIR/app-icon.svg" "$PAYLOAD/usr/share/icons/hicolor/scalable/apps/filemcp.svg"
    find "$PAYLOAD" -type d -exec chmod 0755 {} +
    find "$PAYLOAD" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
}

if want deb; then
    DEB="$DIST_DIR/${PACKAGE}_${VERSION}_${DEB_ARCH}.deb"
    echo "==> Creating $(basename "$DEB")"
    stage_payload
    DEB_ROOT="$BUILD_DIR/deb-root"
    rm -rf "$DEB_ROOT"; cp -a "$PAYLOAD" "$DEB_ROOT"
    install -D -m 0644 linux/packaging/debian/copyright "$DEB_ROOT/usr/share/doc/$PACKAGE/copyright"
    install -d -m 0755 "$DEB_ROOT/DEBIAN"
    INSTALLED_SIZE="$(du -sk --apparent-size "$DEB_ROOT" | cut -f1)"
    cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: $PACKAGE
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: $MAINTAINER
Installed-Size: $INSTALLED_SIZE
Depends: libc6 (>= 2.23), libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3t64 | libssl3 | libssl1.1, libfontconfig1, libx11-6, libice6, libsm6, libxcursor1, libxext6, libxfixes3, libxi6, libxrandr2, git, util-linux, libsecret-tools
Recommends: libgtk-3-0t64 | libgtk-3-0, libgl1, xdg-desktop-portal, openssh-client
Section: utils
Priority: optional
Homepage: $HOMEPAGE
Description: $SUMMARY
 FileMCP gives ChatGPT controlled access to one local workspace through the
 Model Context Protocol. It can read and modify files, run Git operations and,
 only when explicitly enabled, run shell commands.
 .
 The MCP server listens only on loopback and is reached through the bundled
 OpenAI Secure MCP Tunnel client. The runtime API key is stored in the Secret
 Service keyring (GNOME Keyring, KWallet, ...), never in the settings file.
EOF
    find "$DEB_ROOT" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
    dpkg-deb --root-owner-group -Zxz --build "$DEB_ROOT" "$DEB" >/dev/null
    dpkg-deb --info "$DEB" | sed -n '/Package:/,/Homepage:/p'
    ARTIFACTS+=("$DEB")
fi

if want rpm; then
    RPM_TOPDIR="$BUILD_DIR/rpm"
    echo "==> Creating ${PACKAGE}-${VERSION}-1.${RPM_ARCH}.rpm"
    stage_payload
    rm -rf "$RPM_TOPDIR"; mkdir -p "$RPM_TOPDIR"/{BUILD,RPMS,SPECS,SRPMS,SOURCES,BUILDROOT}
    FILELIST="$RPM_TOPDIR/files.list"
    {
        echo "%dir $INSTALL_DIR"
        (cd "$PAYLOAD" && find ".$INSTALL_DIR" -mindepth 1 \( -type f -o -type l \) | sort) | while read -r path; do
            path="${path#.}"
            # Every .txt file in the application directory is a legal notice copied above.
            case "$path" in
                *.txt) echo "%license $path" ;;
                *) echo "$path" ;;
            esac
        done
        echo "/usr/bin/filemcp"
        echo "/usr/share/applications/$APP_ID.desktop"
        echo "/usr/share/metainfo/$APP_ID.metainfo.xml"
        (cd "$PAYLOAD" && find ./usr/share/icons -type f | sort) | sed 's:^\.::'
    } > "$FILELIST"
    SPEC="$RPM_TOPDIR/SPECS/$PACKAGE.spec"
    # Prebuilt, already-stripped payload: no debuginfo extraction, no stripping, no .build-id links
    # (the vendored Go/Rust binaries would otherwise collide with distro packages of the same builds).
    # AutoReqProv is off because the self-contained .NET runtime would advertise private libraries;
    # dependencies are declared explicitly by soname so they resolve on Fedora, RHEL and openSUSE alike.
    cat > "$SPEC" <<EOF
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none
%define _binary_payload w9.xzdio
%define _source_date_epoch_from_changelog 0
%define clamp_mtime_to_source_date_epoch 1
%define use_source_date_epoch_as_buildtime 1

Name:           $PACKAGE
Version:        $VERSION
Release:        1
Summary:        $SUMMARY
License:        Apache-2.0 AND MIT AND BSD-3-Clause AND (MIT OR Unlicense)
URL:            $HOMEPAGE
Packager:       $MAINTAINER
AutoReqProv:    no
Requires:       git
Requires:       /usr/bin/setsid
Requires:       /usr/bin/secret-tool
Requires:       libc.so.6()(64bit)
Requires:       libstdc++.so.6()(64bit)
Requires:       libgcc_s.so.1()(64bit)
Requires:       libz.so.1()(64bit)
Requires:       (libssl.so.3()(64bit) or libssl.so.1.1()(64bit))
Requires:       libfontconfig.so.1()(64bit)
Requires:       libX11.so.6()(64bit)
Requires:       libICE.so.6()(64bit)
Requires:       libSM.so.6()(64bit)
Requires:       libXcursor.so.1()(64bit)
Requires:       libXext.so.6()(64bit)
Requires:       libXfixes.so.3()(64bit)
Requires:       libXi.so.6()(64bit)
Requires:       libXrandr.so.2()(64bit)
Recommends:     libgtk-3.so.0()(64bit)
Recommends:     libGL.so.1()(64bit)
Recommends:     /usr/bin/setpriv
Recommends:     /usr/bin/systemd-inhibit

%description
FileMCP gives ChatGPT controlled access to one local workspace through the
Model Context Protocol. It can read and modify files, run Git operations and,
only when explicitly enabled, run shell commands.

The MCP server listens only on loopback and is reached through the bundled
OpenAI Secure MCP Tunnel client. The runtime API key is stored in the Secret
Service keyring (GNOME Keyring, KWallet, ...), never in the settings file.

%prep

%build

%install
mkdir -p %{buildroot}
cp -a "$PAYLOAD/." %{buildroot}/

%files -f $FILELIST
EOF
    rpmbuild -bb --target "$RPM_ARCH" --quiet \
        --define "_topdir $RPM_TOPDIR" \
        --define "_rpmdir $RPM_TOPDIR/RPMS" \
        --define "_buildrootdir $RPM_TOPDIR/BUILDROOT" \
        "$SPEC"
    BUILT_RPM="$(find "$RPM_TOPDIR/RPMS" -name "${PACKAGE}-${VERSION}-1.*.rpm" | head -n 1)"
    [[ -n "$BUILT_RPM" ]] || die "rpmbuild did not produce a package"
    RPM_OUT="$DIST_DIR/${PACKAGE}-${VERSION}-1.${RPM_ARCH}.rpm"
    cp "$BUILT_RPM" "$RPM_OUT"
    rpm -qp --queryformat 'Name: %{NAME}\nVersion: %{VERSION}-%{RELEASE}\nArch: %{ARCH}\nLicense: %{LICENSE}\n' "$RPM_OUT"
    ARTIFACTS+=("$RPM_OUT")
fi

if [[ ${#ARTIFACTS[@]} -eq 0 ]]; then
    die "no package formats selected"
fi
if [[ "$EXPLICIT_FORMATS" -eq 0 ]]; then
    want deb || echo "note: dpkg-deb not found; skipped .deb"
    want rpm || echo "note: rpmbuild not found; skipped .rpm (Debian/Ubuntu: apt install rpm)"
fi

echo "==> Artifacts"
for artifact in "${ARTIFACTS[@]}"; do
    (cd "$(dirname "$artifact")" && sha256sum "$(basename "$artifact")" | tee "$(basename "$artifact").sha256")
done
