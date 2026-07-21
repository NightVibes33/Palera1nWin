[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot
)

$ErrorActionPreference = "Stop"
$toolchain = Join-Path $PackageRoot "toolchain"
$releaseRoot = Split-Path -Parent $PackageRoot
$logDirectory = Join-Path $PackageRoot "smoke-logs"
$statusLog = Join-Path $logDirectory "smoke-status.txt"
New-Item -ItemType Directory -Force $logDirectory | Out-Null
"Palera1nWin DarkSword deterministic package smoke test" | Set-Content $statusLog -Encoding UTF8

function Write-Status([string]$Message) {
    Add-Content -LiteralPath $statusLog -Value $Message -Encoding UTF8
    Write-Host $Message
}

function Assert-File([string]$RelativePath, [long]$MinimumSize = 1) {
    $path = Join-Path $PackageRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing packaged component: $RelativePath"
    }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt $MinimumSize) {
        throw "Packaged component is too small: $RelativePath ($($item.Length) bytes)"
    }
    Write-Status "OK file $RelativePath ($($item.Length) bytes)"
    return $path
}

function Assert-Pe([string]$RelativePath) {
    $path = Assert-File $RelativePath 512
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $first = $stream.ReadByte()
        $second = $stream.ReadByte()
    }
    finally {
        $stream.Dispose()
    }
    if ($first -ne 0x4D -or $second -ne 0x5A) {
        throw "Packaged executable is not a PE file: $RelativePath"
    }
    Write-Status "OK PE $RelativePath"
}

function Assert-BinaryString([string]$RelativePath, [string]$Expected) {
    $path = Join-Path $PackageRoot $RelativePath
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
    if (-not $ascii.Contains($Expected, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $unicode.Contains($Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Expected capability string '$Expected' was not found in $RelativePath"
    }
    Write-Status "OK capability $RelativePath :: $Expected"
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$Name
    )

    $stdout = Join-Path $logDirectory "$Name.stdout.txt"
    $stderr = Join-Path $logDirectory "$Name.stderr.txt"
    Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $toolchain `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -NoNewWindow `
        -Wait `
        -PassThru

    $output = @()
    if (Test-Path $stdout) { $output += Get-Content $stdout -Raw }
    if (Test-Path $stderr) { $output += Get-Content $stderr -Raw }
    return [PSCustomObject]@{
        ExitCode = $process.ExitCode
        Output = ($output -join [Environment]::NewLine)
    }
}

try {
    $requiredPe = @(
        "Palera1nWin.exe",
        "toolchain\turdus_merula.exe",
        "toolchain\openra1n.exe",
        "toolchain\openra1n-core.exe",
        "toolchain\darksword-pongo.exe",
        "toolchain\wdi-simple.exe",
        "toolchain\libusb-1.0.dll"
    )
    foreach ($relative in $requiredPe) {
        Assert-Pe $relative
    }

    Assert-File "Palera1nWin.dll" 1024 | Out-Null
    Assert-File "DarkSwordRestore.Core.dll" 1024 | Out-Null
    Assert-BinaryString "Palera1nWin.dll" "DowngradeView"
    Assert-BinaryString "Palera1nWin.dll" "DarkSwordRestore.Core"

    Assert-File "toolchain\native-build-manifest.txt" 32 | Out-Null
    Assert-File "toolchain\resources\sep_racer.bin" 128 | Out-Null
    Assert-File "toolchain\resources\kpf.bin" 128 | Out-Null
    Assert-File "manifest.json" 32 | Out-Null

    $turdusHelp = Invoke-CapturedProcess `
        -FilePath (Join-Path $toolchain "turdus_merula.exe") `
        -ArgumentList @("--help") `
        -Name "turdus-help"
    foreach ($operation in @("get-shcblock", "get-pteblock", "load-shcblock")) {
        if (-not $turdusHelp.Output.Contains($operation, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "turdus_merula.exe help output is missing required operation '$operation'. Exit code: $($turdusHelp.ExitCode)"
        }
        Write-Status "OK turdus help operation $operation"
    }

    Assert-BinaryString "toolchain\darksword-pongo.exe" "DarkSword Pongo Bridge"
    Assert-BinaryString "toolchain\darksword-pongo.exe" "--pteblock"
    Assert-BinaryString "toolchain\darksword-pongo.exe" "sep pwn_pte"
    Assert-BinaryString "toolchain\darksword-pongo.exe" "bootux"
    Assert-BinaryString "toolchain\openra1n.exe" "openra1n-core.exe"
    Assert-BinaryString "toolchain\openra1n.exe" "wdi-simple.exe"
    Assert-BinaryString "toolchain\openra1n.exe" "0x4141"

    $nativeManifest = Get-Content (Join-Path $toolchain "native-build-manifest.txt") -Raw
    foreach ($token in @("resource-sha384=", "idevicerestore=", "openra1n=", "libfragmentzip-version=")) {
        if (-not $nativeManifest.Contains($token, [System.StringComparison]::Ordinal)) {
            throw "Native build manifest is missing: $token"
        }
        Write-Status "OK native manifest $token"
    }

    $manifestPath = Join-Path $PackageRoot "manifest.json"
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest -or $manifest.Count -lt 10) {
        throw "Package manifest is empty or incomplete."
    }
    foreach ($entry in $manifest) {
        $relative = ([string]$entry.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $path = Join-Path $PackageRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Manifest references a missing file: $($entry.path)"
        }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne [long]$entry.size) {
            throw "Manifest size mismatch: $($entry.path)"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "Manifest SHA-256 mismatch: $($entry.path)"
        }
    }
    Write-Status "OK manifest verified $($manifest.Count) files"

    $checksumPath = Join-Path $releaseRoot "SHA256SUMS.txt"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Release checksum file is missing."
    }
    $checksumLine = (Get-Content -LiteralPath $checksumPath | Select-Object -First 1).Trim()
    if ($checksumLine -notmatch '^([0-9a-fA-F]{64})\s+(.+)$') {
        throw "Release checksum file has an invalid format."
    }
    $expectedZipHash = $Matches[1].ToLowerInvariant()
    $zipName = $Matches[2].Trim()
    $zipPath = Join-Path $releaseRoot $zipName
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Release ZIP referenced by SHA256SUMS.txt is missing: $zipName"
    }
    $actualZipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualZipHash -ne $expectedZipHash) {
        throw "Release ZIP SHA-256 mismatch."
    }
    Write-Status "OK release ZIP SHA-256 $actualZipHash"
    Write-Status "Unified Palera1nWin package smoke test passed. Physical DFU/restore testing remains required."
}
catch {
    Write-Status "FAILED: $($_.Exception.Message)"
    Write-Status $_.ScriptStackTrace
    throw
}