#!/usr/bin/env bash
# Builds the React UI from a pinned precision-scores commit and lands
# the output at vendor/web/app/ ready to be served by the local
# Kestrel. Zero-touch: the upstream clone is read-only; nothing is
# written back to it.
#
# Usage:  build/web.sh
# Env override: PRECISION_SCORES_SOURCE=/path/to/local/clone
#   (skips the clone/pull dance for faster dev iteration)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
VENDOR_WEB="$ROOT/vendor/web"
UPSTREAM_URL="${PRECISION_SCORES_URL:-https://github.com/mariomoosh01/precision-scores}"
PIN_SHA="${PRECISION_SCORES_SHA:-0f39794}"
LOCAL_SOURCE="${PRECISION_SCORES_SOURCE:-}"

# The local Kestrel address the React build will target. Must stay in
# sync with OfflineHost.BaseUrl in src/Host/OfflineHost.cs.
export VITE_API_BASE_URL="http://127.0.0.1:34567"

mkdir -p "$ROOT/vendor"

if [[ -n "$LOCAL_SOURCE" && -d "$LOCAL_SOURCE" ]]; then
    echo "[web.sh] Using local clone: $LOCAL_SOURCE"
    SRC_DIR="$LOCAL_SOURCE"
else
    SRC_DIR="$VENDOR_WEB/src"
    if [[ ! -d "$SRC_DIR/.git" ]]; then
        echo "[web.sh] Cloning $UPSTREAM_URL into $SRC_DIR"
        git clone --quiet "$UPSTREAM_URL" "$SRC_DIR"
    fi
    echo "[web.sh] Checking out pinned SHA $PIN_SHA"
    git -C "$SRC_DIR" fetch --quiet origin
    git -C "$SRC_DIR" checkout --quiet "$PIN_SHA"
fi

echo "[web.sh] Installing deps (this may take a minute on cold cache)"
(cd "$SRC_DIR" && npm ci --silent --no-audit --no-fund)

echo "[web.sh] Building React with VITE_API_BASE_URL=$VITE_API_BASE_URL"
(cd "$SRC_DIR" && CI=false npm run build)

# Copy the build output to a known location the .NET Content item
# points at. We copy rather than symlink so a `dotnet publish` on a
# fresh checkout picks it up without a prior build step.
rm -rf "$VENDOR_WEB/app"
mkdir -p "$VENDOR_WEB/app"
cp -R "$SRC_DIR/app/." "$VENDOR_WEB/app/"

echo "[web.sh] Build output -> $VENDOR_WEB/app ($(du -sh "$VENDOR_WEB/app" | cut -f1))"

# Zero-touch audit: if we used the local clone, confirm the build didn't
# modify any TRACKED file. (-uno skips untracked files that may have been
# there before this script ran.)
if [[ -n "$LOCAL_SOURCE" ]]; then
    if [[ -n "$(git -C "$LOCAL_SOURCE" status --porcelain -uno)" ]]; then
        echo "[web.sh] WARN: upstream clone has modified tracked files — zero-touch guarantee broken!" >&2
        git -C "$LOCAL_SOURCE" status --short -uno >&2
        exit 2
    fi
fi
