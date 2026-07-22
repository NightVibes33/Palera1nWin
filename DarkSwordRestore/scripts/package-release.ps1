[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$NativeDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = "dev"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $OutputDirectory "DarkSword-Restore-win-x64"
$toolchain = Join-Path $stage "toolchain"
$toolchainResources = Join-Path $toolchain "resources"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item $stage -ItemType Directory -Force | Out-Null
New-Item $toolchain -ItemType Directory -Force | Out-Null
New-Item $toolchainResources -ItemType Directory -Force | Out-Null

Copy-Item (Join-Path $PublishDirectory "*") $stage -Recurse -Force
Copy-Item (Join-Path $NativeDirectory "*") $toolchain -Recurse -Force

$appExecutable = Join-Path $stage "Palera1nWin.exe"
if (-not (Test-Path $appExecutable -PathType Leaf)) {
    throw "The unified Palera1nWin.exe frontend was not published."
}

$required = @(
    "openra1n.exe",
    "openra1n-core.exe",
    "turdus_merula.exe",
    "darksword-pongo.exe",
    "wdi-simple.exe",
    "gaster.exe",
    "ideviceinfo.exe",
    "irecovery.exe",
    "libusb-1.0.dll",
    "resources\sep_racer.bin",
    "resources\kpf.bin"
)
foreach ($relativePath in $required) {
    $path = Join-Path $toolchain $relativePath
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Missing packaged component: $relativePath"
    }
    if ((Get-Item $path).Length -eq 0) {
        throw "Packaged component is empty: $relativePath"
    }
}

Copy-Item (Join-Path $projectRoot "README.md") (Join-Path $stage "README-DARKSWORD.md") -Force
Copy-Item (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") (Join-Path $stage "THIRD_PARTY_NOTICES.md") -Force

@{
    toolchainRoot = "toolchain"
    wslDistro = "Ubuntu"
    selectedReleaseTag = "v2.3"
    jailbreakMode = "rootless"
    safeMode = $false
    verboseBoot = $true
    debugLogging = $true
    autoInstallDrivers = $true
    checkUpdates = $true
    preferUsbA = $true
} | ConvertTo-Json | Set-Content (Join-Path $stage "settings.json") -Encoding UTF8

@"
Palera1nWin + DarkSword Restore $Version

1. Extract the complete ZIP before running it.
2. Install Apple Devices or desktop iTunes so Windows has Apple's normal and recovery drivers.
3. Right-click Palera1nWin.exe and choose Run as administrator.
4. Open the Downgrade page and connect a supported A9, A9X, A10, or A10X iPhone, iPad, or iPod touch.
5. Browse to an untouched iOS/iPadOS 15 IPSW.
6. If the device is already in black-screen DFU, click Downgrade Selected IPSW. Detection, inspection, matching, and USB preparation are automatic.
7. Use the timed DFU guide only when the device is not already in DFU. A cable/computer image is Recovery Mode.
8. Keep the generated session folder and all tether-boot assets backed up.

The active Windows SHC/PTE restore and PTE tether-boot backend currently covers A9/A9X.
A10/A10X uses a separate personalized iBoot/SEP boot path and must never be routed through the A9 sequence.
Palera1nWin.exe and the toolchain folder must remain together.
"@ | Set-Content (Join-Path $stage "START-HERE.txt") -Encoding UTF8

$stageFullPath = [System.IO.Path]::GetFullPath($stage)
$manifest = Get-ChildItem $stageFullPath -Recurse -File | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($stageFullPath, $_.FullName).Replace('\', '/')
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [PSCustomObject]@{ path = $relative; sha256 = $hash; size = $_.Length }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $stageFullPath "manifest.json") -Encoding UTF8

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$zip = Join-Path $OutputDirectory "DarkSword-Restore-$Version-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stageFullPath "*") -DestinationPath $zip -CompressionLevel Optimal

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $(Split-Path $zip -Leaf)" | Set-Content (Join-Path $OutputDirectory "SHA256SUMS.txt") -Encoding ASCII
Write-Host "Packaged $zip"
