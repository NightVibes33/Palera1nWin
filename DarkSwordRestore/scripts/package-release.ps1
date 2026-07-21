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
$toolchainNative = Join-Path $stage "toolchain\native"
$toolchainResources = Join-Path $stage "toolchain\resources"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item $stage -ItemType Directory -Force | Out-Null
New-Item $toolchainNative -ItemType Directory -Force | Out-Null
New-Item $toolchainResources -ItemType Directory -Force | Out-Null

Copy-Item (Join-Path $PublishDirectory "*") $stage -Recurse -Force

$resourceNames = @("sep_racer.bin", "kpf.bin", "cpf.bin", "overlay.bin", "union.bin")
Get-ChildItem $NativeDirectory -File | ForEach-Object {
    if ($resourceNames -contains $_.Name) {
        Copy-Item $_.FullName (Join-Path $toolchainResources $_.Name) -Force
    }
    else {
        Copy-Item $_.FullName (Join-Path $toolchainNative $_.Name) -Force
    }
}

@"
DarkSword Restore $Version

1. Extract the complete ZIP before running it.
2. Install Apple Devices or desktop iTunes so Windows has Apple's normal/recovery drivers.
3. Right-click DarkSwordRestore.exe and choose Run as administrator.
4. Use a direct USB-A to Lightning connection whenever possible.

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
