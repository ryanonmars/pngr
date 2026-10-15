# Pngr

[![CI](https://github.com/ryanonmars/pngr/actions/workflows/ci.yml/badge.svg)](https://github.com/ryanonmars/pngr/actions/workflows/ci.yml)

Pngr is a standalone network diagnostics application for macOS and Windows, built with .NET 10, C#, Avalonia, and SQLite. It monitors multiple targets using ICMP, TCP, or UDP and keeps session history locally.

- Independent target monitoring, pause/resume, and probing profiles.
- Hop latency, jitter, and diagnostic response-loss statistics.
- Multiple synchronized historical graphs with panning, zooming, and interval selection.
- Saved sessions, workspace restoration, route-change events, and optional alerts.
- Optional read-only web dashboard and REST API, disabled by default, with authenticated LAN access.

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
scripts/package-universal.sh 0.8.0

# Windows cross-publish/installer (also works from macOS)
scripts/package.sh win-x64 0.8.0
python3 scripts/package-windows-installer.py 0.8.0
```

```powershell
# Native Windows publish and per-user installer
./scripts/package.ps1 -Version 0.8.0
python scripts/package-windows-installer.py 0.8.0
```

The updater SDK and CLI are pinned to Velopack 1.2.161; the packaging scripts restore the repository's local tool manifest. Final assets are in `artifacts/installers/osx-universal` and `artifacts/installers/win-x64`. Architecture-specific ZIPs remain internal build intermediates/portable local packages, not future public release downloads. Run from source with `dotnet run --project src/TraceScope.App`. The existing solution and internal namespaces retain the TraceScope name.

## Measurements and existing data

Response loss counts eligible diagnostic probes without qualifying replies. It does not prove forwarding loss; intermediate routers can suppress diagnostic replies while forwarding traffic. A UDP destination ICMP Port Unreachable response establishes destination reachability, not UDP application-service availability.

Data is stored locally. Existing TraceScope settings, profiles, workspaces, and sessions remain compatible. Installed builds retain `~/Library/Application Support/TraceScope` on macOS and `%LOCALAPPDATA%\TraceScope` on Windows, with the existing `tracescope.db` filename. The macOS identifier remains `dev.tracescope.desktop`. Source-checkout launches use `artifacts/data`; `--data-dir PATH` selects an explicit location.

## Web dashboard and REST API

Open **Settings → Web Server** to enable the existing read-only server. **Localhost Only** is the default: `http://127.0.0.1:8787` works without authentication on the computer running Pngr. The desktop application never requires web authentication.

To connect another device, explicitly select **Local Network (LAN)**, keep **Automatic (Recommended)** or choose a private address under **Advanced network options**, set the port, acknowledge the HTTP warning, and apply. Copy the displayed server address and the newly generated `pngr_` access key. Use that address in a browser and enter the key, or send `Authorization: Bearer <access-key>` with API requests. In LAN mode, authentication applies to both the LAN and localhost listeners. The API remains read-only; existing response formats are unchanged.

The original key is available only when generated in the desktop settings window; save it in a password manager before closing the window. Pngr persists a SHA-256 verifier, not the original secret. **Regenerate Key** requires confirmation and immediately invalidates the previous key and all browser sessions. A lost key must be regenerated. Existing macOS Keychain and Windows Credential Manager webhook storage is unchanged.

LAN HTTP is **unencrypted**: someone who can observe or alter network traffic can capture the key, browser session, and monitoring data. Use a trusted isolated network or an encrypted VPN/tunnel; a user-managed HTTPS reverse proxy needs the restrictions described in the [Web Server guide](website/content/docs/settings/local-web-api.mdx). Pngr does not provision TLS certificates, router forwarding, UPnP, or firewall exceptions. LAN access does not itself enable internet access; the network owner remains responsible for routing, firewall and proxy exposure.

Choose **Localhost Only** and apply to remove LAN access, or disable **Enable Web Server** to stop both listeners. Pngr must remain open. Automatic selection uses the active default-route interface and a suitable private IPv4 address, excluding known VPN and virtual adapters. Existing manual addresses are preserved. A running LAN listener never switches networks silently; unavailable addresses and port conflicts are reported without changing monitoring. Reapply Web Server settings or restart Pngr to resolve Automatic again. See the [Web Server guide](website/content/docs/settings/local-web-api.mdx) and [API reference](website/content/docs/advanced/local-api.mdx) for login, API examples and troubleshooting.

The web dashboard has separate, synchronized latency and jitter graphs with independent scales. Wheel or drag to zoom, use Earlier/Later to navigate, and choose Live to follow new samples. Fit uses available samples within the selected history range; Reset Zoom restores that range. Show Jitter is a browser-only preference. See [web graph controls](website/content/docs/graphs-history/navigation.mdx#web-dashboard-navigation).

## Automated releases

Builds and tests cover ARM64 macOS, Intel macOS and Windows x64 on GitHub-hosted runners. During a release, the final validation job checks updater package versions, SHA-256 digests and archive integrity, rejects private data, and generates `SHA256SUMS` for the installers and updater assets.

Normal pushes and pull requests run builds and automated tests on all three platforms without installer packaging. Version tags run tests, package each platform once, assemble the universal macOS DMG from those artifacts, validate the same installer/update assets, and publish only after every required check passes. NuGet and DMG tooling dependencies are cached. Already published tags are skipped; existing releases are never overwritten. Release packaging is reserved for version tags.

To validate updating, use an installed v0.7.1 or later release and check manually, review the release notes, download, approve installation, and verify the new version after restart. Compare history, profiles and workspace before/after. Repeat with an interrupted download/offline check and verify the existing app remains usable. Apple Silicon testing does not establish Intel or Windows runtime behavior.


## Third-party licenses

Application distributions include `THIRD-PARTY-NOTICES.txt` and an exact
dependency inventory. macOS bundles keep notices in `Contents/Resources`;
Windows installations keep them in the application’s `current` directory.
The DMG also includes a notice file at its root. Website notices are linked
from the footer and documentation navigation.

See [desktop licenses and inventory](compliance/README.md) and
[website licenses and inventory](website/compliance/README.md). Pngr-owned code and documentation use the [MIT license](LICENSE). Third-party
components retain their own licenses. Branding and artwork are excluded from
the code license pending confirmation of their source rights.
Dependency or runtime changes require an inventory review before packaging.

Maintainers can run the existing CI workflow manually with
`verify_packages=true` and an internal prerelease `package_version` to validate
all installers and update packages without creating a tag or publishing a release.
