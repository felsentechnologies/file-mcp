# Changelog

All notable changes will be documented in this file from the first public release onward.

## Unreleased

- Added a compact macOS workspace overview, top navigation and a persistent light/dark appearance toggle.
- Improved connection-action contrast, workspace alignment and layout regression coverage across tabs, providers and runtime states.
- Updated setup documentation and added native screenshots for all macOS screens in light and dark appearance.

- Simplified Connection with an active-session summary, full-width MCP endpoint, grouped copy actions and collapsible Cloudflare management.

- Redesigned the OAuth login page with responsive light/dark styling, accessible code entry and explicit access details; styles use a per-response CSP nonce.

### Added

- Native Linux client: an Avalonia desktop app on the shared C# `FileMCP.Core`.
  - **Packaging:** self-contained `.deb`, `.rpm` and portable `.tar.gz` packages for x86_64 and ARM64 (`build_linux_app.sh`), with a desktop entry, icons and AppStream metadata.
  - **Desktop:** system-tray (StatusNotifierItem) lifecycle and single-instance activation.
  - **Credentials:** runtime API key stored in the Secret Service keyring; no plaintext fallback.
  - **State:** XDG configuration and data directories.
  - **Processes and power:** `setsid` process-group cleanup, `setpriv --pdeathsig` crash cleanup, and a systemd-logind sleep inhibitor while connected.
- Vendored official `tunnel-client` v0.0.12 and ripgrep 15.2.0 (static musl) binaries for `linux-amd64` and `linux-arm64`, with provenance, checksums and license files.
- Linux CI job.
  - **Tests:** shared-core integration suite (real GNOME Keyring), Xvfb app smoke test and x64/ARM64 package builds.
  - **Install checks:** `.deb`/`.rpm` installs on Debian 12, Ubuntu 22.04/24.04, Fedora and openSUSE Tumbleweed; ARM64 runs under QEMU.
- Optional Cloudflare Tunnel provider (macOS): use a dashboard token or locally authenticated named tunnel with a public hostname. OAuth is the default; the legacy secret-URL mode is opt-in.
- Open-source project license and contribution/security documentation.
- GitHub Actions macOS, Windows and Linux CI verification.
- Reproducible source-archive packaging from tracked Git content only.
- Explicit provenance and checksum documentation for the vendored OpenAI `tunnel-client` binary.
- New FileMCP macOS app icon optimized for the Dock.
- Native Windows application with WPF UI, system-tray lifecycle, Windows Credential Manager storage, and x64/ARM64 release packaging.
- Windows parity integration coverage for filesystem containment, NTFS junction/reparse points, Git safe mode, Job Object process cleanup, MCP legacy/modern protocols, and tunnel runtime lifecycle.
- Cross-platform `save_conversation_to_codex` support for importing supplied conversations into Codex local history on macOS and Windows, with post-write app-server verification.
- Cross-platform `batch_read` support for combining up to 16 read-only file, search, and Git inspection operations into one MCP round trip.
- Cross-platform ranked `search_code` with multi-query single-pass scanning, transparent relevance signals, and coverage metadata.
- Cross-platform ripgrep-backed `grep` and `glob` tools modeled on coding-agent search: regex/literal patterns, glob and file-type filters, content/count/file output modes, context lines, newest-first file ordering, and `head_limit`/`offset` pagination.
- Bundled official ripgrep 15.2.0 binaries (macOS arm64, Windows x64/ARM64) with documented provenance, checksums, and license files.
- Per-tool-call timing and response-size log lines (tool name only; no arguments or content).
- Cross-platform `repo_overview` for factual repository structure, manifest, extension-count, exclusion, and scan-coverage context.
- Cross-platform `workspace_context` for scoped coding context, including Git status, applicable `AGENTS.md`, bounded manifest contents, and top-level workspace entries.
- Cross-platform conflict-checked `edit_file` and batched `apply_patch` tools with dry-run previews, SHA-256 guards, ordered multi-hunk edits, pre-write validation, and best-effort rollback of partial multi-file writes.
- Cross-platform session command tools (`start_command`, `read_command_output`, and `cancel_command`) with bounded cursor-based streaming, idempotent request IDs, timeout/cancellation states, retained output, and descendant-process cleanup.

### Changed

- `FileMCP.Core` now targets `net8.0` and is shared by the Windows and Linux apps. Platform-specific behavior sits behind `FileMcpPlatform`.
  - **Windows:** behavior and storage locations are unchanged.
  - **Linux paths:** containment uses `realpath(3)` and case-sensitive comparisons.
  - **Linux processes and environment:** environment snapshots are case-sensitive, `run_command` uses the login shell, and the Git safe-mode SSH command uses OpenSSH's `-F /dev/null`.
  - **All platforms:** edits now keep Unix permission bits.
- Fixed `workspace_context` and other structured tool outputs failing to serialize on current .NET 8 servicing runtimes when they contained string array entries added at runtime (for example error lists). This affected Windows too.
- Redesigned the FileMCP identity with a flat folded-document symbol with a custom folded M monogram, crimson (#DB0F30) palette, layered gradient artwork and light/dark/monochrome vector logos, transparent PNGs and matching macOS/Windows application icons. Added a reproducible brand-kit exporter.
- Cloudflare on macOS now defaults to OAuth with PKCE, a private app login code, hashed Keychain session storage, rotating refresh tokens and revocation on replay or permission/workspace changes. Secret-URL authentication requires explicit compatibility opt-in.
- macOS HTTP transport now bounds active connections, request-read time and response-write time, and cancels sockets on shutdown.
- Added separate, default-off macOS permissions for Git push and writing to Codex history, plus full-history/source secret scanning in CI.
- Improved the macOS Connection, Workspace and Activity tabs with a persistent connection action, workspace/access summary, cancellable startup, inline connection errors, log-copy feedback and an empty activity state. Configuration is locked while sharing; Quit remains in the standard app menu.
- Cloudflare startup now waits for edge registration, recognizes registration messages split across reads, checks local tunnel credentials, and uses the same origin certificate for management and connection. Tunnel log redaction also handles secrets split across stdout/stderr reads.
- Replaced the hand-written recursive scanners behind `search_code` and `repo_overview` with ripgrep, and replaced `search_content`/`search_filenames` with `grep`/`glob`. Searches now respect `.gitignore`/`.ignore` by default (opt out with `include_ignored`), always reject explicit `.git` targets, and avoid prematurely exhausting recursive-scan byte budgets on binary or build-output trees while reporting bounded/truncated coverage explicitly.

- Updated the bundled OpenAI `tunnel-client` to v0.0.12 with upstream NOTICE and third-party license evidence, including official Windows AMD64/ARM64 binaries.
- Rebranded the macOS app and MCP server identity from Local Files MCP to FileMCP.
- Updated the app bundle/executable names, default profile, default shared folder, documentation, and release artifact naming for FileMCP.
- Removed the persistent menu-bar status item; FileMCP now relies on the Dock and the standard macOS application menu.
- Made advanced settings resize the window between compact and expanded states.
- Polished the native macOS UI with a wider layout, clearer connection-state actions, English labels/errors/runtime messages, collapsible advanced settings, and standard App/Edit/Window menus.
- Preserved the existing bundle identifier and Keychain namespace so local settings and saved runtime credentials continue to migrate safely across the rebrand.
- Hardened local HTTP parsing with required local authentication, strict `Host`/`Origin` and header syntax checks, bounded framing, and rejection of trailing body bytes.
- Added a fresh per-runtime 256-bit token between `tunnel-client` and the loopback MCP server; the token is referenced through an environment indirection, not persisted in generated tunnel profiles, and redacted from logs/errors.
- Restricted the `tunnel-client` health/admin listener to loopback, isolated FileMCP tunnel profiles under Application Support, and allowlisted the child tunnel environment to prevent ambient config overrides.
- Hardened Git safe mode across Git/config/common/object/alternate metadata, repository config includes, embedded repositories, external init templates, HTTP credential/TLS file settings, credential helpers, SSH config execution paths, and MCP-internal TOCTOU races.
- Hardened process execution by validating launch inputs before spawn and cleaning descendant process trees on parent exit, timeout, stop, and shutdown (POSIX process groups on macOS; Job Objects on Windows).
- HTTP malformed-request fuzz iterations can be reduced through `MCP_HTTP_FUZZ_ITERATIONS` for targeted development/CI runs while preserving the full default count.
- Scoped recursive filename search can reduce unnecessary local traversal when callers already know the relevant subdirectory, without changing default broad-search coverage.
- Content search now reports explicit truncation reasons so callers can distinguish result, preview, visited-entry, and byte-budget limits.
