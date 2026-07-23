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
$runtimeSource = Join-Path $projectRoot "runtime\jailbreak"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item $stage -ItemType Directory -Force | Out-Null
New-Item $toolchain -ItemType Directory -Force | Out-Null

Copy-Item (Join-Path $PublishDirectory "*") $stage -Recurse -Force
Copy-Item (Join-Path $NativeDirectory "*") $toolchain -Recurse -Force
if (-not (Test-Path $runtimeSource -PathType Container)) {
    throw "Packaged Jailbreak runtime source is missing: $runtimeSource"
}
Copy-Item (Join-Path $runtimeSource "*") $toolchain -Recurse -Force

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
    "ideviceinfo.exe",
    "irecovery.exe",
    "libusb-1.0.dll",
    "resources\sep_racer.bin",
    "resources\kpf.bin",
    "palera1n.cmd",
    "windows\palera1n.ps1",
    "build\fake-checkra1n.sh",
    "build\provision-wsl.sh",
    "dist\palera1n-linux-x86_64"
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

$linuxBinary = Join-Path $toolchain "dist\palera1n-linux-x86_64"
if ((Get-Item $linuxBinary).Length -lt 65536) {
    throw "The packaged palera1n Linux binary is unexpectedly small."
}
$elf = [IO.File]::ReadAllBytes($linuxBinary)[0..3]
if ($elf[0] -ne 0x7F -or $elf[1] -ne 0x45 -or $elf[2] -ne 0x4C -or $elf[3] -ne 0x46) {
    throw "The packaged palera1n Linux runtime is not an ELF executable."
}

Copy-Item (Join-Path $projectRoot "README.md") (Join-Path $stage "README-DARKSWORD.md") -Force
Copy-Item (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") (Join-Path $stage "THIRD_PARTY_NOTICES.md") -Force

@"
Palera1nWin + DarkSword Restore $Version

1. Extract the complete ZIP before running it.
2. Install Apple Devices or desktop iTunes, WSL, and usbipd-win.
3. Run Palera1nWin.exe as administrator.
4. Setup > Run Doctor now validates the packaged Jailbreak and DarkSword runtime.
5. Keep exactly one Apple device connected during any hardware operation.
6. Jailbreak: Provision WSL once, enter DFU, then use Start Jailbreak.
7. Downgrade: inspect an exact-device iOS/iPadOS 15 IPSW and pass Test DFU -> PongoOS before erase.
8. Keep the complete DarkSword session folder and boot-profile.json backed up for every cold boot.

The active Windows SHC/PTE restore and PTE tether-boot backend currently covers A9/A9X.
A10/A10X detection and firmware download are present, but their separate personalized boot backend remains disabled.
Palera1nWin.exe and the complete toolchain folder must remain together.
"@ | Set-Content (Join-Path $stage "START-HERE.txt") -Encoding UTF8

$stageFullPath = [IO.Path]::GetFullPath($stage)
$manifest = Get-ChildItem $stageFullPath -Recurse -File | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stageFullPath, $_.FullName).Replace('\', '/')
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [PSCustomObject]@{ path = $relative; sha256 = $hash; size = $_.Length }
}
$manifestPath = Join-Path $stageFullPath "manifest.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding UTF8

# Keep a second detached checksum for the manifest itself. The application validates
# every listed runtime file before allowing Administrator-level hardware operations.
$manifestHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$manifestHash  manifest.json" | Set-Content (Join-Path $stageFullPath "manifest.sha256") -Encoding ASCII

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$zip = Join-Path $OutputDirectory "DarkSword-Restore-$Version-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stageFullPath "*") -DestinationPath $zip -CompressionLevel Optimal

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $(Split-Path $zip -Leaf)" | Set-Content (Join-Path $OutputDirectory "SHA256SUMS.txt") -Encoding ASCII
Write-Host "Packaged $zip"
