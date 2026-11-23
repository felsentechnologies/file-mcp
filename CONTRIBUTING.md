# Contributing

Contributions are welcome. Because this project can read and mutate local files and optionally execute commands, changes should preserve the existing security boundaries rather than bypass them for convenience.

## Development requirements

### macOS

- macOS 12 or newer;
- Xcode or Xcode Command Line Tools with `swiftc`;
- the vendored `tunnel-client` binary for the current architecture.

Build and test:

```bash
./build_macos_app.sh
./tests/test_swift_runtime.sh
bash tests/test_swift_ui.sh
bash tests/test_swift_agent_tools.sh
# Install Gitleaks before running this publication check:
bash tests/check_secrets.sh
```

For a quicker parser-fuzz pass while developing:

```bash
MCP_HTTP_FUZZ_ITERATIONS=20 ./tests/test_swift_runtime.sh
```

### Windows

- Windows 10/11;
- .NET 8 SDK;
- Git for Windows;
- the vendored `tunnel-client.exe` for the target architecture.

Build and test from PowerShell:

```powershell
dotnet build windows/FileMCP.Windows.sln -c Release -warnaserror
./tests/test_windows_runtime.ps1
```

Build distributable packages with:

```powershell
./build_windows_app.ps1 -Architecture x64
./build_windows_app.ps1 -Architecture arm64
```

The Windows integration suite intentionally exercises Windows Credential Manager, junction/reparse-point containment, Job Object process cleanup, Git for Windows safe mode, raw MCP/HTTP behavior, and tunnel-runtime lifecycle.

### Linux

- a glibc-based distribution (Debian/Ubuntu/Mint, Fedora, RHEL, openSUSE) with an X11 or XWayland session for the GUI;
- .NET 8 SDK and Git;
- `dpkg-deb` for `.deb` packages and `rpmbuild` (`apt install rpm` / `dnf install rpm-build`) for `.rpm` packages;
- optional: `secret-tool` (`libsecret-tools`) with a Secret Service provider, `Xvfb` and `dbus-run-session` for the smoke test.

The Linux app (`linux/src/FileMCP.Linux`, Avalonia) shares `windows/src/FileMCP.Core` with Windows. Core changes must keep both platforms working: guard platform behavior with `FileMcpPlatform`/`OperatingSystem` checks and run both suites.

Build and test:

```bash
dotnet build linux/FileMCP.Linux.sln -c Release -warnaserror
./tests/test_linux_runtime.sh
./run_linux_dev.sh            # debug run with the vendored helpers
```

Build distributable packages and smoke-test the archive:

```bash
./build_linux_app.sh --arch x64 --formats tar,deb,rpm
./build_linux_app.sh --arch arm64 --formats tar,deb,rpm
./tests/test_linux_app.sh x64
```

Set `FILEMCP_PACKAGE_MAINTAINER="Name <email>"` to stamp the maintainer/packager field of published packages.

The Linux integration suite exercises:

- `realpath` symlink containment;
- case-sensitive paths;
- `setsid` process-group cleanup and the `setpriv --pdeathsig` crash cleanup;
- `flock` profile locks;
- XDG directories;
- the Secret Service through `secret-tool`.

For a headless run against a real keyring:

```bash
dbus-run-session -- bash -c 'printf pass | gnome-keyring-daemon --unlock --components=secrets >/dev/null; FILEMCP_TEST_SECRET_SERVICE=real ./tests/test_linux_runtime.sh'
```

## Change guidelines

- Inspect the owning code and existing tests before editing.
- Preserve equivalent MCP tool/protocol behavior across macOS, Windows and Linux unless a documented platform constraint makes equivalence impossible.
- Keep both the MCP listener and tunnel health/admin listener loopback-only unless the security model is deliberately redesigned and reviewed.
- Do not weaken shared-directory containment, symlink/reparse-point handling, Git metadata/config checks, MCP-local authentication, tunnel profile/environment isolation, process cleanup, request-size limits, or argument validation.
- Keep `run_command` opt-in and clearly destructive.
- Add or update platform-specific regression tests for behavior/security changes.
- Do not commit generated `build/`, `dist/`, `bin/`, or `obj/` output.
- Never commit API keys, OAuth stores, tokens, local Git credentials, Keychain/Credential Manager exports, `.env` files, or archives of a working directory.

## Vendored tunnel-client updates

When updating `vendor/tunnel-client`:

1. use an official `openai/tunnel-client` release;
2. verify every bundled platform archive against upstream `SHA256SUMS.txt`;
3. verify each extracted binary by SHA-256;
4. update `vendor/tunnel-client/VERSION` and its provenance documentation;
5. keep the upstream `LICENSE`, `NOTICE`, and platform third-party license evidence with each binary;
6. update the pinned checksums in `.github/workflows/verify.yml` (macOS, Windows and Linux jobs);
7. run the relevant macOS, Windows and Linux test/build jobs.

## Vendored ripgrep updates

When updating `vendor/ripgrep`:

1. use an official `BurntSushi/ripgrep` GitHub release;
2. verify every bundled platform archive against the `.sha256` file published with the same release;
3. verify each extracted binary by SHA-256 and update the pinned checksums in `.github/workflows/verify.yml`;
4. update `vendor/ripgrep/VERSION` and `vendor/ripgrep/README.md`;
5. keep `COPYING`, `LICENSE-MIT`, and `UNLICENSE` from the release archive;
6. run the relevant macOS, Windows and Linux test/build jobs, including the search integration tests.

## Pull requests

Describe the behavior changed, security impact, affected platforms, and exact verification commands/results. Keep unrelated refactors out of security-sensitive patches so review remains auditable.
