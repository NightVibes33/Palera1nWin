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

$required = @(
    "openra1n.exe",
    "openra1n-core.exe",
    "turdus_merula.exe",
    "darksword-pongo.exe",
    "wdi-simple.exe",
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

Copy-Item (Join-Path $projectRoot "README.md") (Join-Path $stage "README.md") -Force
Copy-Item (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") (Join-Path $stage "THIRD_PARTY_NOTICES.md") -Force

@"
DarkSword Restore $Version

1. Extract the complete ZIP before running it.
2. Install Apple Devices or desktop iTunes so Windows has Apple's normal/recovery drivers.
3. Right-click DarkSwordRestore.exe and choose Run as administrator.
4. Use a direct USB-A to Lightning connection whenever possible.
5. Keep the generated session folder and PTE block backed up.

The app and its toolchain must remain in the same folder.
"@ | Set-Content (Join-Path $stage "START-HERE.txt") -Encoding UTF8

$manifest = Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [PSCustomObject]@{ path = $relative; sha256 = $hash; size = $_.Length }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $stage "manifest.json") -Encoding UTF8

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$zip = Join-Path $OutputDirectory "DarkSword-Restore-$Version-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $(Split-Path $zip -Leaf)" | Set-Content (Join-Path $OutputDirectory "SHA256SUMS.txt") -Encoding ASCII
Write-Host "Packaged $zip"
