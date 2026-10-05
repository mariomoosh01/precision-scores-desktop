#!/usr/bin/env bash
# Builds the scanner-service into a single-file native executable via
# PyInstaller. Zero-touch: the upstream clone is read-only.
#
# Output: vendor/scanner/scanner        (the executable)
#         vendor/scanner/mnist.onnx     (CNN weights, loaded via env var)
#
# Usage:  build/scanner.sh
# Env override: PRECISION_SCORES_SOURCE=/path/to/local/clone
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
VENDOR_SCAN="$ROOT/vendor/scanner"
UPSTREAM_URL="${PRECISION_SCORES_URL:-https://github.com/mariomoosh01/precision-scores}"
PIN_SHA="${PRECISION_SCORES_SHA:-0f39794}"
LOCAL_SOURCE="${PRECISION_SCORES_SOURCE:-}"
# Must match the Dockerfile's pin so dev + prod use the same MNIST weights.
MNIST_URL="${MNIST_URL:-https://github.com/onnx/models/raw/c5cae0f1942c8d7727372c7b1f6b485aa39e4421/validated/vision/classification/mnist/model/mnist-8.onnx}"

mkdir -p "$ROOT/vendor"
rm -rf "$VENDOR_SCAN"; mkdir -p "$VENDOR_SCAN"

if [[ -n "$LOCAL_SOURCE" && -d "$LOCAL_SOURCE" ]]; then
    echo "[scanner.sh] Using local precision-scores clone: $LOCAL_SOURCE"
    REPO_DIR="$LOCAL_SOURCE"
else
    REPO_DIR="$ROOT/vendor/scanner-src"
    if [[ ! -d "$REPO_DIR/.git" ]]; then
        echo "[scanner.sh] Cloning $UPSTREAM_URL into $REPO_DIR"
        git clone --quiet "$UPSTREAM_URL" "$REPO_DIR"
    fi
    echo "[scanner.sh] Checking out pinned SHA $PIN_SHA"
    git -C "$REPO_DIR" fetch --quiet origin
    git -C "$REPO_DIR" checkout --quiet "$PIN_SHA"
fi

SCANNER_DIR="$REPO_DIR/scanner-service"
if [[ ! -d "$SCANNER_DIR" ]]; then
    echo "[scanner.sh] ERROR: scanner-service/ not found in $REPO_DIR" >&2
    exit 2
fi

# macOS pyzbar needs libzbar.dylib bundled. Locate it (brew prefix differs
# by architecture).
case "$(uname -m)" in
    arm64) BREW_PREFIX="/opt/homebrew" ;;
    x86_64) BREW_PREFIX="/usr/local" ;;
    *) BREW_PREFIX="/usr/local" ;;
esac
LIBZBAR="$BREW_PREFIX/lib/libzbar.0.dylib"
if [[ ! -f "$LIBZBAR" ]]; then
    echo "[scanner.sh] libzbar not found at $LIBZBAR. Install with: brew install zbar" >&2
    exit 3
fi

# Build in an isolated venv next to the upstream clone so we don't
# pollute the clone's own venv.
BUILD_VENV="$ROOT/vendor/scanner-build-venv"
rm -rf "$BUILD_VENV"
python3 -m venv "$BUILD_VENV"
# shellcheck disable=SC1091
source "$BUILD_VENV/bin/activate"
pip install --quiet --upgrade pip
pip install --quiet \
    'opencv-contrib-python-headless>=4.10' \
    'numpy>=1.26' \
    'pyzbar>=0.1.9' \
    'onnxruntime>=1.19' \
    'fastapi>=0.115' \
    'uvicorn[standard]>=0.32' \
    'python-multipart>=0.0.9' \
    'pyinstaller>=6.0'

# Download the MNIST ONNX next to the bundle (not INSIDE the PyInstaller
# archive) so the shell can set MNIST_MODEL_PATH at launch time without
# knowing the _MEIPASS temp dir.
curl -sSL "$MNIST_URL" -o "$VENDOR_SCAN/mnist.onnx"
echo "[scanner.sh] mnist.onnx $(wc -c < "$VENDOR_SCAN/mnist.onnx") bytes"

# PyInstaller build. --add-binary bundles libzbar inside the archive so
# pyzbar can find it after extraction. --collect-all onnxruntime catches
# the native libs that onnxruntime ships.
BUILD_WORK="$ROOT/vendor/scanner-pyinstaller"
rm -rf "$BUILD_WORK"; mkdir -p "$BUILD_WORK"
pushd "$BUILD_WORK" >/dev/null
pyinstaller \
    --onefile \
    --name scanner \
    --paths "$SCANNER_DIR" \
    --hidden-import onnxruntime \
    --collect-all onnxruntime \
    --add-binary "$LIBZBAR:." \
    "$SCANNER_DIR/service/main.py"
popd >/dev/null

cp "$BUILD_WORK/dist/scanner" "$VENDOR_SCAN/scanner"
chmod +x "$VENDOR_SCAN/scanner"
echo "[scanner.sh] Scanner binary -> $VENDOR_SCAN/scanner ($(du -sh "$VENDOR_SCAN/scanner" | cut -f1))"

deactivate

if [[ -n "$LOCAL_SOURCE" ]]; then
    if [[ -n "$(git -C "$LOCAL_SOURCE" status --porcelain -uno)" ]]; then
        echo "[scanner.sh] WARN: upstream clone has modified tracked files!" >&2
        git -C "$LOCAL_SOURCE" status --short -uno >&2
        exit 4
    fi
fi
