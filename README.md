# Precision Scores Desktop

Offline-capable Windows + macOS desktop app that wraps the Precision Scores
React UI, hosts a local Kestrel server, and bundles the Python scanner
sidecar so match directors can score cards without an internet connection.

See [plan](#plan) for the full design.

## Hard constraint

**Zero changes to the three upstream repos** this app depends on:

| Upstream | How this app uses it |
|---|---|
| `precisionScoresApi` | HTTP API only, from the sync agent, over the public cloud |
| `precision-scores` (React) | Build artifact (`npm run build` with `VITE_API_BASE_URL` pinned to localhost); embedded as resources |
| `scanner-service` | PyInstaller-compiled single-file sidecar binary |

The upstream repos are cloned at pinned commit SHAs during the build and
never modified. `git status` in each upstream must be clean after any
desktop build.

## Project layout

```
src/
  Shell/      Photino window host + status strip + connectivity monitor (console app, exe entry point)
  Host/       ASP.NET Core Kestrel on 127.0.0.1:34567 + SQLite via EF Core (class library)
  Sync/       Cloud HTTP client + download/upload sync workflows (class library)
  Scanner/    Subprocess manager for the PyInstaller'd scanner sidecar (class library)
tests/
  Shell.Tests/
build/
  web.ps1 + web.sh         Pin-and-fetch React build
  scanner.ps1 + scanner.sh Pin-and-fetch + PyInstaller the sidecar
  installer.ps1 + installer.sh
installer/
  win/   WiX config for .msi
  mac/   Info.plist, entitlements, create-dmg script
```

## Local dev

```
dotnet build
dotnet run --project src/Shell
```

## Plan

Full design (phases A–H, zero-touch rationale, verification steps) lives in
the planning doc that bootstrapped this repo. Open `~/.claude/plans/virtual-dazzling-goose.md`
if you need the long form.
