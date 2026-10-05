#!/usr/bin/env bash
# Builds the macOS installer: self-contained .NET publish → .app bundle
# → .dmg disk image. Assumes:
#   - vendor/web/app/ already populated by build/web.sh
#   - vendor/scanner/ already populated by build/scanner.sh (optional;
#     missing just means the scanner sidecar is absent and opencv.js
#     fallback runs)
#
# Output: dist/PrecisionScoresDesktop.dmg
#
# Code-signing + notarization are OUT OF SCOPE for v1 — this script
# produces an unsigned bundle. Users will see Gatekeeper's "cannot be
# opened" dialog on first launch and must right-click → Open to bypass.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
DIST="$ROOT/dist"
APP_BUNDLE="$DIST/PrecisionScoresDesktop.app"
RID="${RID:-osx-arm64}"
CONFIG="${CONFIG:-Release}"

if [[ "$(uname)" != "Darwin" ]]; then
    echo "[installer.sh] This script only runs on macOS." >&2
    exit 1
fi

rm -rf "$DIST"; mkdir -p "$DIST"

echo "[installer.sh] dotnet publish -r $RID -c $CONFIG"
dotnet publish "$ROOT/src/Shell/Shell.csproj" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained=true \
    -p:PublishSingleFile=false \
    -o "$DIST/publish"

mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"
cp -R "$DIST/publish/." "$APP_BUNDLE/Contents/MacOS/"
cp "$ROOT/installer/mac/Info.plist" "$APP_BUNDLE/Contents/Info.plist"

# The Shell is the actual exe entry point. Mark it executable; dotnet
# publish usually gets this right but belt-and-braces.
chmod +x "$APP_BUNDLE/Contents/MacOS/PrecisionScoresDesktop.Shell"

echo "[installer.sh] .app bundle size: $(du -sh "$APP_BUNDLE" | cut -f1)"

# Build the .dmg via hdiutil (no brew dep). Uses UDZO compression and
# bakes the bundle into a drag-to-Applications layout.
DMG_STAGE="$DIST/dmg-stage"
mkdir -p "$DMG_STAGE"
cp -R "$APP_BUNDLE" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"

DMG_OUT="$DIST/PrecisionScoresDesktop.dmg"
rm -f "$DMG_OUT"
hdiutil create -volname "Precision Scores Desktop" \
    -srcfolder "$DMG_STAGE" \
    -ov -format UDZO \
    "$DMG_OUT"

rm -rf "$DMG_STAGE"
echo "[installer.sh] .dmg -> $DMG_OUT ($(du -sh "$DMG_OUT" | cut -f1))"
