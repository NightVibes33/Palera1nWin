# ==========================================================================
#  bundle-release.ps1
#
#  Builds a portable, end-user-ready Palera1nWin release zip:
#    Palera1nWin.exe            (self-contained single-file WPF GUI)
#    toolchain\                 (openra1n, palera1n launcher, palera1n-linux
#                               binary, driver tooling, provision scripts)
#
#  The zip is fully self-contained: an end user only needs WSL2 + usbipd-win
#  installed system-wide. The GUI auto-discovers toolchain\ next to the exe.
#
#  Usage (from repo root, admin not required):
#     .\tools\bundle-release.ps1
#     .\tools\bundle-release.ps1 -ToolchainSource E:\Work\Palera1n-Windows
#     .\tools\bundle-release.ps1 -PublishOutput publish-v100 -ZipName Palera1nWin-win-x64.zip
# ==========================================================================
[CmdletBinding()]
param(
    [string]$ToolchainSource = "E:\Work\Palera1n-Windows",
    [string]$PublishOutput   = "publish-v100",
    [string]$ZipName         = "Palera1nWin-win-x64.zip",
    [string]$Configuration   = "Release"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Test-Path $ToolchainSource)) {
    throw "Toolchain source not found: $ToolchainSource`nSet -ToolchainSource to your Palera1n-Windows checkout."
}

# ---- 1. publish the GUI as a self-contained single-file exe ----------------
Write-Host "[bundle] publishing Palera1nWin ($Configuration, win-x64, single-file)..." -ForegroundColor Cyan
$publishDir = Join-Path $repoRoot $PublishOutput
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# IncludeNativeLibrariesForSelfExtract + EnableCompressionInSingleFile embed the
# WPF native DLLs (D3DCompiler, wpfgfx, PenImc, vcruntime140_cor3, ...) into ONE
# compressed exe (~77 MB) so the release folder is just Palera1nWin.exe + toolchain\.
dotnet publish src\Palera1nWin.App -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Drop PDBs — end users do not need symbol files.
Get-ChildItem $publishDir -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# ---- 2. stage the runtime toolchain ---------------------------------------
Write-Host "[bundle] staging toolchain into $PublishOutput\toolchain\..." -ForegroundColor Cyan
$tcDst = Join-Path $publishDir "toolchain"
if (Test-Path $tcDst) { Remove-Item $tcDst -Recurse -Force }

$dirs = @(
    "dist\openra1n-win",
    "dist\native",
    "windows",
    "build\runtime"
)
foreach ($d in $dirs) {
    New-Item -ItemType Directory -Path (Join-Path $tcDst $d) -Force | Out-Null
}

# (source-relative, dest-relative) pairs. Only files actually referenced by
# the GUI / launcher at runtime are bundled — no build tooling, no source.
$files = @(
    @("dist\openra1n-win\openra1n.exe",       "dist\openra1n-win\openra1n.exe"),
    @("dist\openra1n-win\libusb-1.0.dll",     "dist\openra1n-win\libusb-1.0.dll"),
    @("dist\native\wdi-simple.exe",           "dist\native\wdi-simple.exe"),
    @("dist\native\zadig.exe",                 "dist\native\zadig.exe"),
    @("dist\native\gaster.exe",                "dist\native\gaster.exe"),
    # gaster.exe (CLI --native-pwn path) links libcrypto + its own libusb build.
    @("dist\native\libcrypto-1_1-x64.dll",     "dist\native\libcrypto-1_1-x64.dll"),
    @("dist\native\libusb-1.0.dll",            "dist\native\libusb-1.0.dll"),
    @("dist\palera1n-linux-x86_64",            "dist\palera1n-linux-x86_64"),
    @("palera1n.cmd",                          "palera1n.cmd"),
    @("windows\palera1n.ps1",                  "windows\palera1n.ps1"),
    @("windows\lib.ps1",                        "windows\lib.ps1"),
    @("windows\usb-bridge.ps1",                "windows\usb-bridge.ps1"),
    @("windows\pwn-native.ps1",                 "windows\pwn-native.ps1"),
    @("build\fake-checkra1n.sh",               "build\fake-checkra1n.sh"),
    @("build\stop-amds.ps1",                   "build\stop-amds.ps1"),
    @("build\provision-wsl.sh",                "build\provision-wsl.sh"),
    @("build\runtime\pln-run.sh",               "build\runtime\pln-run.sh")
)

$missing = @()
foreach ($f in $files) {
    $s = Join-Path $ToolchainSource $f[0]
    $d = Join-Path $tcDst $f[1]
    if (Test-Path $s) {
        Copy-Item -Path $s -Destination $d -Force
    } else {
        $missing += $f[0]
    }
}
if ($missing.Count -gt 0) {
    Write-Host "[bundle] MISSING toolchain files:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "    $_" }
    throw "Toolchain incomplete. Build/assemble the missing files in $ToolchainSource first."
}

# ---- 3. verify the staged toolchain matches Paths.ValidateToolchain --------
$required = @(
    "dist\openra1n-win\openra1n.exe",
    "palera1n.cmd",
    "build\fake-checkra1n.sh"
)
foreach ($r in $required) {
    if (-not (Test-Path (Join-Path $tcDst $r))) {
        throw "Validation failed: $r missing from staged toolchain."
    }
}

$totalMB = [math]::Round((Get-ChildItem $tcDst -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
Write-Host "[bundle] staged toolchain: $totalMB MB, $((Get-ChildItem $tcDst -Recurse -File).Count) files" -ForegroundColor Green

# ---- 3b. license / attribution files (required: we redistribute 3rd-party bins) --
Write-Host "[bundle] staging THIRD_PARTY_NOTICES.md + licenses\..." -ForegroundColor Cyan
$noticeSrc = Join-Path $repoRoot "THIRD_PARTY_NOTICES.md"
if (-not (Test-Path $noticeSrc)) { throw "THIRD_PARTY_NOTICES.md missing at repo root." }
Copy-Item $noticeSrc (Join-Path $publishDir "THIRD_PARTY_NOTICES.md") -Force

$licSrc = Join-Path $repoRoot "licenses"
if (-not (Test-Path $licSrc)) { throw "licenses\ folder missing at repo root." }
$licDst = Join-Path $publishDir "licenses"
if (Test-Path $licDst) { Remove-Item $licDst -Recurse -Force }
Copy-Item $licSrc $licDst -Recurse -Force
Write-Host "[bundle] staged $((Get-ChildItem $licDst -File).Count) license files" -ForegroundColor Green

# ---- 4. zip it -------------------------------------------------------------
$zipPath = Join-Path $repoRoot $ZipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "[bundle] compressing -> $ZipName..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

$zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "[bundle] done. $ZipName = $zipMB MB" -ForegroundColor Green
Write-Host "        Path: $zipPath"
