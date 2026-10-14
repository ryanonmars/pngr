# Pngr

[![CI](https://github.com/ryanonmars/pngr/actions/workflows/ci.yml/badge.svg)](https://github.com/ryanonmars/pngr/actions/workflows/ci.yml)

Pngr is a standalone network diagnostics application for macOS and Windows, built with .NET 10, C#, Avalonia, and SQLite. It monitors multiple targets using ICMP, TCP, or UDP and keeps session history locally.

- Independent target monitoring, pause/resume, and probing profiles.
- Hop latency, jitter, and diagnostic response-loss statistics.
- Multiple synchronized historical graphs with panning, zooming, and interval selection.
- Saved sessions, workspace restoration, route-change events, and optional alerts.
- Optional read-only localhost API, disabled by default.

No background monitoring service or cloud account is required. Closing Pngr stops monitoring and the optional API.

## Install

The existing [v0.7.0 release](https://github.com/ryanonmars/pngr/releases/tag/v0.7.0) retains its three portable ZIP downloads. The [v0.7.1 release](https://github.com/ryanonmars/pngr/releases/tag/v0.7.1) introduced installers and in-app updates. Current installer packages use the following names:

| Platform | Installer |
| --- | --- |
| macOS Apple Silicon and Intel | `Pngr-macos-universal.dmg` |
| Windows x64 | `Pngr-Setup.exe` |

On macOS, open the DMG and drag the **Pngr** app to Applications. The app contains native ARM64 and Intel runtime payloads selected by a universal launcher. On Windows, run Setup to install under your user account without administrator access; its wizard provides welcome, progress and completion screens plus Start Menu/Desktop shortcuts and an uninstall entry. The wizard wraps the existing Velopack installer; subsequent in-app updates keep using Velopack. No separate .NET installation is needed. The installed bundle/executable and Velopack package identity retain the lowercase `pngr` names for update compatibility.

Packages are unsigned and macOS packages are not notarized (they have ad-hoc integrity signatures). Gatekeeper and SmartScreen may warn or block launching them; use the operating system's normal approval interface only if you trust the download. Pngr does not disable or bypass these protections.

### Updates

Pngr checks for newer stable GitHub releases in the background, at most approximately once every 24 hours. Use **Pngr → Check for Updates…** on macOS or **File → Check for Updates…** on Windows for a manual check. An available update can be viewed or deferred; release notes and download progress are shown before installation, and installation always requires your approval. Updating restarts Pngr and preserves its separate user-data directory.

Automatic installation requires a supported installed location: an app copied from the DMG to a writable Applications folder on macOS, or a Windows installation made by Setup. Portable v0.7.0/source-checkout builds require a manual installer migration. Permissions or operating-system security restrictions may also require manual installation. Offline checks and failed downloads leave the running installation intact.

Releases also contain platform-specific Velopack feeds (`releases.win-x64.json` and `releases.osx-universal.json`), full `.nupkg` update packages, and `SHA256SUMS`. These are updater assets; users should choose the DMG or Setup. The checksums detect download corruption and are verified before installation; unsigned GitHub assets are not a substitute for a trusted developer signature.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). macOS packaging also needs Xcode Command Line Tools; Windows packaging uses PowerShell and NSIS 3.12 (`choco install nsis --version=3.12.0 --yes --no-progress`). For Windows installer cross-compilation on macOS, install `makensis` with Homebrew. Package verification uses Python 3.

```sh
dotnet restore TraceScope.slnx
dotnet build TraceScope.slnx -c Release
dotnet test TraceScope.slnx -c Release
```

Build installers locally:

```sh
# macOS universal app, DMG and macOS update feed (run on a Mac)
scripts/package-universal.sh 0.7.4

# Windows cross-publish/installer (also works from macOS)
scripts/package.sh win-x64 0.7.4
python3 scripts/package-windows-installer.py 0.7.4
```

```powershell
# Native Windows publish and per-user installer
./scripts/package.ps1 -Version 0.7.4
python scripts/package-windows-installer.py 0.7.4
```

The updater SDK and CLI are pinned to Velopack 1.2.161; the packaging scripts restore the repository's local tool manifest. Final assets are in `artifacts/installers/osx-universal` and `artifacts/installers/win-x64`. Architecture-specific ZIPs remain internal build intermediates/portable local packages, not future public release downloads. Run from source with `dotnet run --project src/TraceScope.App`. The existing solution and internal namespaces retain the TraceScope name.

## Measurements and existing data

Response loss counts eligible diagnostic probes without qualifying replies. It does not prove forwarding loss; intermediate routers can suppress diagnostic replies while forwarding traffic. A UDP destination ICMP Port Unreachable response establishes destination reachability, not UDP application-service availability.

Data is stored locally. Existing TraceScope settings, profiles, workspaces, and sessions remain compatible. Installed builds retain `~/Library/Application Support/TraceScope` on macOS and `%LOCALAPPDATA%\TraceScope` on Windows, with the existing `tracescope.db` filename. The macOS identifier remains `dev.tracescope.desktop`. Source-checkout launches use `artifacts/data`; `--data-dir PATH` selects an explicit location.

## Automated releases

Builds and tests cover ARM64 macOS, Intel macOS and Windows x64 on GitHub-hosted runners. During a release, the final validation job checks updater package versions, SHA-256 digests and archive integrity, rejects private data, and generates `SHA256SUMS` for the installers and updater assets.

Normal pushes and pull requests run builds and automated tests on all three platforms without installer packaging. Version tags run tests, package each platform once, assemble the universal macOS DMG from those artifacts, validate the same installer/update assets, and publish only after every required check passes. NuGet and DMG tooling dependencies are cached. Already published tags are skipped; existing releases are never overwritten. Release packaging is reserved for version tags.

To validate updating, use an installed v0.7.1 or later release and check manually, review the release notes, download, approve installation, and verify the new version after restart. Compare history, profiles and workspace before/after. Repeat with an interrupted download/offline check and verify the existing app remains usable. Apple Silicon testing does not establish Intel or Windows runtime behavior.
