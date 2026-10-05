# Builds the Windows installer: self-contained .NET publish + harvest
# with heat.exe → WiX build → .msi.
#
# Requires WiX v4 as a dotnet global tool. Script installs it on first
# run if missing.
#
# Output: dist/PrecisionScoresDesktop.msi
#
# Code-signing (Authenticode) is OUT OF SCOPE for v1 — unsigned installers
# will show a SmartScreen warning on first launch.
$ErrorActionPreference = "Stop"

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Here
$Dist = Join-Path $Root "dist"
$PublishDir = Join-Path $Dist "publish-win-x64"
$Rid = $env:RID; if (-not $Rid) { $Rid = "win-x64" }
$Config = $env:CONFIG; if (-not $Config) { $Config = "Release" }

if (-not $IsWindows) {
    Write-Error "[installer.ps1] This script only runs on Windows."
}

if (Test-Path $Dist) { Remove-Item -Recurse -Force $Dist }
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

Write-Host "[installer.ps1] dotnet publish -r $Rid -c $Config"
dotnet publish (Join-Path $Root "src/Shell/Shell.csproj") `
    -c $Config `
    -r $Rid `
    --self-contained=true `
    -p:PublishSingleFile=false `
    -o $PublishDir

# Install WiX as a global tool if not present. Idempotent.
$wixInstalled = (dotnet tool list --global | Select-String "^wix\s") -ne $null
if (-not $wixInstalled) {
    Write-Host "[installer.ps1] Installing WiX v4 as dotnet global tool"
    dotnet tool install --global wix
    # Also install UI extension — WixUI_InstallDir dialog pulled in from there.
    wix extension add -g WixToolset.UI.wixext
}

# Harvest the publish output into a Fragment WiX can reference. heat is
# shipped as part of the WiX tool but at v4 it's integrated into `wix build`
# via a HarvestDirectory input. For simplicity we use the standalone
# approach: generate a ComponentGroup fragment first.
$HarvestOut = Join-Path $Dist "publish-harvest.wxs"
Write-Host "[installer.ps1] Harvesting publish dir -> $HarvestOut"
wix extension add -g WixToolset.Util.wixext 2>$null
# heat.exe isn't in WiX v4; use `wix harvest` fragment.
# Fall back to invoking the shipped harvester:
$harvestArgs = @(
    "harvest",
    "dir", $PublishDir,
    "-cg", "PublishedFiles",
    "-dr", "INSTALLFOLDER",
    "-gg", "-sfrag", "-srd", "-suid",
    "-var", "var.PublishDir",
    "-o", $HarvestOut
)
# WiX v4's harvest command isn't bundled with the main tool yet; using
# WixToolset.Heat as a separate tool.
dotnet tool install --global WixToolset.Heat.Tool 2>$null
heat @harvestArgs

Write-Host "[installer.ps1] wix build"
$MsiOut = Join-Path $Dist "PrecisionScoresDesktop.msi"
wix build `
    (Join-Path $Root "installer/win/Product.wxs") `
    $HarvestOut `
    -d PublishDir=$PublishDir `
    -ext WixToolset.UI.wixext `
    -o $MsiOut

Write-Host "[installer.ps1] .msi -> $MsiOut ($([math]::Round((Get-Item $MsiOut).Length/1MB, 1)) MB)"
