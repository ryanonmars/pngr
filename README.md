# pngr

[![CI](https://github.com/ryanonmars/pngr/actions/workflows/ci.yml/badge.svg)](https://github.com/ryanonmars/pngr/actions/workflows/ci.yml)

pngr is a standalone network diagnostics application for macOS and Windows, built with .NET 10, C#, Avalonia, and SQLite. It monitors multiple targets using ICMP, TCP, or UDP and keeps session history locally.

- Independent target monitoring, pause/resume, and probing profiles.
- Hop latency, jitter, and diagnostic response-loss statistics.
- Multiple synchronized historical graphs with panning, zooming, and interval selection.
- Saved sessions, workspace restoration, route-change events, and optional alerts.
- Optional read-only localhost API, disabled by default.

No background monitoring service or cloud account is required. Closing pngr stops monitoring and the optional API.

## Install

Download the archive for your platform from [Releases](https://github.com/ryanonmars/pngr/releases):

| Platform | Archive |
| --- | --- |
| macOS Apple Silicon | `pngr-osx-arm64.zip` |
| macOS Intel | `pngr-osx-x64.zip` |
| Windows x64 | `pngr-win-x64.zip` |

Extract the macOS archive and move `pngr.app` to Applications. On Windows, extract the entire archive into a folder and run `pngr.exe`; keep its accompanying runtime files together. No separate .NET installation is needed.

Initial packages are unsigned and macOS packages are not notarized. Your operating system may prompt before opening them. macOS packages use an ad-hoc signature for Apple Silicon compatibility; no developer signing certificate is required.

The first release has not been published yet. Build artifacts are available from successful [Actions runs](https://github.com/ryanonmars/pngr/actions).

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). macOS packaging also needs Xcode Command Line Tools; Windows packaging uses PowerShell. Package verification uses Python 3.

```sh
dotnet restore TraceScope.slnx
dotnet build TraceScope.slnx -c Release
dotnet test TraceScope.slnx -c Release
```

Build self-contained distributions:

```sh
# macOS (run on a Mac)
scripts/package.sh osx-arm64 0.7.0
scripts/package.sh osx-x64 0.7.0
```

```powershell
# Windows
./scripts/package.ps1 -Version 0.7.0
```

The packages replace `artifacts/pngr-<runtime>.zip`, rather than accumulating versioned copies. Run from source with `dotnet run --project src/TraceScope.App`. The existing solution and internal namespaces retain the TraceScope name.

## Measurements and existing data

Response loss counts eligible diagnostic probes without qualifying replies. It does not prove forwarding loss; intermediate routers can suppress diagnostic replies while forwarding traffic. A UDP destination ICMP Port Unreachable response establishes destination reachability, not UDP application-service availability.

Data is stored locally. Existing TraceScope settings, profiles, workspaces, and sessions remain compatible. Installed builds retain `~/Library/Application Support/TraceScope` on macOS and `%LOCALAPPDATA%\TraceScope` on Windows, with the existing `tracescope.db` filename. The macOS identifier remains `dev.tracescope.desktop`. Source-checkout launches use `artifacts/data`; `--data-dir PATH` selects an explicit location.

## Automated releases

Pushes and pull requests build, test, and package all three platforms on GitHub-hosted runners. Version tags such as `v0.7.0` run the same checks and publish the three stable asset names above. The tag version is embedded in the application and macOS bundle metadata.

The Release workflow's manual dispatch is **build-only validation**: it never creates a tag or release. The first version tag will be pushed only after validation and explicit approval. No in-app updater is implemented.
