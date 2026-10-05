# Builds the React UI from a pinned precision-scores commit and lands
# the output at vendor/web/app/ ready to be served by the local
# Kestrel. Zero-touch: the upstream clone is read-only.
#
# Usage:  pwsh build/web.ps1
# Env override: $env:PRECISION_SCORES_SOURCE = "C:\path\to\local\clone"
$ErrorActionPreference = "Stop"

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Here
$VendorWeb = Join-Path $Root "vendor/web"
$UpstreamUrl = $env:PRECISION_SCORES_URL
if (-not $UpstreamUrl) { $UpstreamUrl = "https://github.com/mariomoosh01/precision-scores" }
$PinSha = $env:PRECISION_SCORES_SHA
if (-not $PinSha) { $PinSha = "0f39794" }
$LocalSource = $env:PRECISION_SCORES_SOURCE

# Must match OfflineHost.BaseUrl in src/Host/OfflineHost.cs.
$env:VITE_API_BASE_URL = "http://127.0.0.1:34567"

New-Item -ItemType Directory -Force -Path (Join-Path $Root "vendor") | Out-Null

if ($LocalSource -and (Test-Path $LocalSource -PathType Container)) {
    Write-Host "[web.ps1] Using local clone: $LocalSource"
    $SrcDir = $LocalSource
} else {
    $SrcDir = Join-Path $VendorWeb "src"
    if (-not (Test-Path (Join-Path $SrcDir ".git"))) {
        Write-Host "[web.ps1] Cloning $UpstreamUrl into $SrcDir"
        git clone --quiet $UpstreamUrl $SrcDir
    }
    Write-Host "[web.ps1] Checking out pinned SHA $PinSha"
    git -C $SrcDir fetch --quiet origin
    git -C $SrcDir checkout --quiet $PinSha
}

Write-Host "[web.ps1] Installing deps (this may take a minute on cold cache)"
Push-Location $SrcDir
npm ci --silent --no-audit --no-fund
Pop-Location

Write-Host "[web.ps1] Building React with VITE_API_BASE_URL=$env:VITE_API_BASE_URL"
Push-Location $SrcDir
$env:CI = "false"
npm run build
Pop-Location

$AppOut = Join-Path $VendorWeb "app"
if (Test-Path $AppOut) { Remove-Item -Recurse -Force $AppOut }
New-Item -ItemType Directory -Force -Path $AppOut | Out-Null
Copy-Item -Recurse -Force (Join-Path $SrcDir "app/*") $AppOut

$Size = (Get-ChildItem $AppOut -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host "[web.ps1] Build output -> $AppOut ($([math]::Round($Size/1MB, 1)) MB)"

if ($LocalSource) {
    $Dirty = git -C $LocalSource status --porcelain -uno
    if ($Dirty) {
        Write-Error "[web.ps1] WARN: upstream clone has modified tracked files — zero-touch guarantee broken!`n$Dirty"
    }
}
