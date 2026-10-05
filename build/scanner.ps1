# Builds the scanner-service into a single-file Windows .exe via
# PyInstaller. Zero-touch: the upstream clone is read-only.
#
# Output: vendor/scanner/scanner.exe   (the executable)
#         vendor/scanner/mnist.onnx    (CNN weights, loaded via env var)
$ErrorActionPreference = "Stop"

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Here
$VendorScan = Join-Path $Root "vendor/scanner"
$UpstreamUrl = $env:PRECISION_SCORES_URL
if (-not $UpstreamUrl) { $UpstreamUrl = "https://github.com/mariomoosh01/precision-scores" }
$PinSha = $env:PRECISION_SCORES_SHA
if (-not $PinSha) { $PinSha = "0f39794" }
$LocalSource = $env:PRECISION_SCORES_SOURCE
$MnistUrl = $env:MNIST_URL
if (-not $MnistUrl) {
    $MnistUrl = "https://github.com/onnx/models/raw/c5cae0f1942c8d7727372c7b1f6b485aa39e4421/validated/vision/classification/mnist/model/mnist-8.onnx"
}

if (Test-Path $VendorScan) { Remove-Item -Recurse -Force $VendorScan }
New-Item -ItemType Directory -Force -Path $VendorScan | Out-Null

if ($LocalSource -and (Test-Path $LocalSource -PathType Container)) {
    Write-Host "[scanner.ps1] Using local precision-scores clone: $LocalSource"
    $RepoDir = $LocalSource
} else {
    $RepoDir = Join-Path $Root "vendor/scanner-src"
    if (-not (Test-Path (Join-Path $RepoDir ".git"))) {
        Write-Host "[scanner.ps1] Cloning $UpstreamUrl into $RepoDir"
        git clone --quiet $UpstreamUrl $RepoDir
    }
    Write-Host "[scanner.ps1] Checking out pinned SHA $PinSha"
    git -C $RepoDir fetch --quiet origin
    git -C $RepoDir checkout --quiet $PinSha
}

$ScannerDir = Join-Path $RepoDir "scanner-service"
if (-not (Test-Path $ScannerDir)) {
    Write-Error "[scanner.ps1] scanner-service/ not found in $RepoDir"
}

# pyzbar's Windows wheel ships libzbar-64.dll — PyInstaller picks it up
# automatically from the wheel's package dir, no --add-binary needed.
$BuildVenv = Join-Path $Root "vendor/scanner-build-venv"
if (Test-Path $BuildVenv) { Remove-Item -Recurse -Force $BuildVenv }
python -m venv $BuildVenv
& "$BuildVenv/Scripts/Activate.ps1"
pip install --quiet --upgrade pip
pip install --quiet `
    'opencv-contrib-python-headless>=4.10' `
    'numpy>=1.26' `
    'pyzbar>=0.1.9' `
    'onnxruntime>=1.19' `
    'fastapi>=0.115' `
    'uvicorn[standard]>=0.32' `
    'python-multipart>=0.0.9' `
    'pyinstaller>=6.0'

Invoke-WebRequest -Uri $MnistUrl -OutFile (Join-Path $VendorScan "mnist.onnx")
Write-Host "[scanner.ps1] mnist.onnx $(Get-Item (Join-Path $VendorScan 'mnist.onnx')).Length bytes"

$BuildWork = Join-Path $Root "vendor/scanner-pyinstaller"
if (Test-Path $BuildWork) { Remove-Item -Recurse -Force $BuildWork }
New-Item -ItemType Directory -Force -Path $BuildWork | Out-Null

Push-Location $BuildWork
pyinstaller `
    --onefile `
    --name scanner `
    --paths $ScannerDir `
    --hidden-import onnxruntime `
    --collect-all onnxruntime `
    (Join-Path $ScannerDir "service/main.py")
Pop-Location

Copy-Item (Join-Path $BuildWork "dist/scanner.exe") (Join-Path $VendorScan "scanner.exe")
$Size = (Get-Item (Join-Path $VendorScan "scanner.exe")).Length
Write-Host "[scanner.ps1] Scanner binary -> $(Join-Path $VendorScan 'scanner.exe') ($([math]::Round($Size/1MB, 1)) MB)"

deactivate

if ($LocalSource) {
    $Dirty = git -C $LocalSource status --porcelain -uno
    if ($Dirty) {
        Write-Error "[scanner.ps1] WARN: upstream clone has modified tracked files!`n$Dirty"
    }
}
