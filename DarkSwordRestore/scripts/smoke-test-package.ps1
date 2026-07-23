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
"Palera1nWin complete packaged-runtime deterministic smoke test" | Set-Content $statusLog -Encoding UTF8

function Write-Status([string]$Message) {
    Add-Content -LiteralPath $statusLog -Value $Message -Encoding UTF8
    Write-Host $Message
}

function Assert-File([string]$RelativePath, [long]$MinimumSize = 1) {
    $path = Join-Path $PackageRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing packaged component: $RelativePath" }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt $MinimumSize) { throw "Packaged component is too small: $RelativePath ($($item.Length) bytes)" }
    Write-Status "OK file $RelativePath ($($item.Length) bytes)"
    return $path
}

function Assert-Pe([string]$RelativePath) {
    $path = Assert-File $RelativePath 512
    $stream = [System.IO.File]::OpenRead($path)
    try { $first = $stream.ReadByte(); $second = $stream.ReadByte() }
    finally { $stream.Dispose() }
    if ($first -ne 0x4D -or $second -ne 0x5A) { throw "Packaged executable is not a PE file: $RelativePath" }
    Write-Status "OK PE $RelativePath"
}

function Assert-Elf([string]$RelativePath) {
    $path = Assert-File $RelativePath 4096
    $bytes = [System.IO.File]::ReadAllBytes($path)[0..3]
    if ($bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        throw "Packaged Linux runtime is not ELF: $RelativePath"
    }
    Write-Status "OK ELF $RelativePath"
}

function Assert-TextContains([string]$RelativePath, [string]$Expected) {
    $path = Assert-File $RelativePath
    $text = Get-Content -LiteralPath $path -Raw
    if (-not $text.Contains($Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Expected text '$Expected' was not found in $RelativePath"
    }
    Write-Status "OK text $RelativePath :: $Expected"
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

function Assert-BinaryDoesNotContain([string]$RelativePath, [string]$Unexpected) {
    $path = Join-Path $PackageRoot $RelativePath
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
    if ($ascii.Contains($Unexpected, [System.StringComparison]::OrdinalIgnoreCase) -or
        $unicode.Contains($Unexpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected legacy capability string '$Unexpected' was found in $RelativePath"
    }
    Write-Status "OK legacy capability absent $RelativePath :: $Unexpected"
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
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $toolchain `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -NoNewWindow -Wait -PassThru
    $output = @()
    if (Test-Path $stdout) { $output += Get-Content $stdout -Raw }
    if (Test-Path $stderr) { $output += Get-Content $stderr -Raw }
    return [PSCustomObject]@{ ExitCode = $process.ExitCode; Output = ($output -join [Environment]::NewLine) }
}

function Invoke-BoundedCapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$TimeoutMilliseconds = 30000
    )
    $stdout = Join-Path $logDirectory "$Name.stdout.txt"
    $stderr = Join-Path $logDirectory "$Name.stderr.txt"
    Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $PackageRoot `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { $process.Kill($true) } catch { }
        throw "$Name exceeded the $TimeoutMilliseconds ms smoke-test timeout."
    }
    $output = @()
    if (Test-Path $stdout) { $output += Get-Content $stdout -Raw }
    if (Test-Path $stderr) { $output += Get-Content $stderr -Raw }
    return [PSCustomObject]@{ ExitCode = $process.ExitCode; Output = ($output -join [Environment]::NewLine) }
}

try {
    $requiredPe = @(
        "Palera1nWin.exe",
        "toolchain\turdus_merula.exe",
        "toolchain\openra1n.exe",
        "toolchain\openra1n-core.exe",
        "toolchain\darksword-pongo.exe",
        "toolchain\wdi-simple.exe",
        "toolchain\ideviceinfo.exe",
        "toolchain\irecovery.exe",
        "toolchain\libusb-1.0.dll"
    )
    foreach ($relative in $requiredPe) { Assert-Pe $relative }

    foreach ($relative in @(
        "toolchain\palera1n.cmd",
        "toolchain\windows\palera1n.ps1",
        "toolchain\build\fake-checkra1n.sh",
        "toolchain\build\provision-wsl.sh"
    )) { Assert-File $relative 64 | Out-Null }
    Assert-Elf "toolchain\dist\palera1n-linux-x86_64"
    Assert-TextContains "toolchain\palera1n.cmd" "palera1n.ps1"
    Assert-TextContains "toolchain\windows\palera1n.ps1" "usbipd"
    Assert-TextContains "toolchain\build\provision-wsl.sh" "pln-run.sh"
    Assert-TextContains "toolchain\build\fake-checkra1n.sh" "checkra1n"

    Assert-File "Palera1nWin.dll" 1024 | Out-Null
    Assert-File "DarkSwordRestore.Core.dll" 1024 | Out-Null
    foreach ($capability in @(
        "DowngradeView",
        "HardwareOperationCoordinator",
        "PackageIntegrityVerifier",
        "ValidateExactHardware_Click",
        "StartIdentityBoundDowngrade_Click",
        "ImportPortableBootProfile_Click",
        "Redacted support export"
    )) { Assert-BinaryString "Palera1nWin.dll" $capability }
    foreach ($capability in @(
        "CompatibilityAssessmentService",
        "RecoveryIntegrityValidator",
        "RedactedSessionExportService",
        "DarkSwordBootProfile",
        "ValidateDfuToPongoAsync",
        "DowngradeStagePlan",
        "--pwned-dfu-only",
        "PWND:[yolo]",
        "DarkSwordJailbroken"
    )) { Assert-BinaryString "DarkSwordRestore.Core.dll" $capability }

    Assert-File "toolchain\native-build-manifest.txt" 32 | Out-Null
    Assert-File "toolchain\native-SHA256SUMS.txt" 32 | Out-Null
    Assert-File "toolchain\resources\sep_racer.bin" 128 | Out-Null
    Assert-File "toolchain\resources\kpf.bin" 128 | Out-Null
    Assert-File "manifest.json" 32 | Out-Null

    $identityHelp = Invoke-CapturedProcess -FilePath (Join-Path $toolchain "ideviceinfo.exe") -ArgumentList @("--help") -Name "ideviceinfo-help"
    if (-not $identityHelp.Output.Contains("ProductType", [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $identityHelp.Output.Contains("key", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ideviceinfo.exe did not expose its key-query interface. Exit code: $($identityHelp.ExitCode)"
    }
    Write-Status "OK ideviceinfo exact ProductType query support"

    $recoveryHelp = Invoke-CapturedProcess -FilePath (Join-Path $toolchain "irecovery.exe") -ArgumentList @("--help") -Name "irecovery-help"
    if (-not $recoveryHelp.Output.Contains("query", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "irecovery.exe did not expose recovery/DFU query support. Exit code: $($recoveryHelp.ExitCode)"
    }
    Write-Status "OK irecovery recovery/DFU identity support"

    $turdusHelp = Invoke-CapturedProcess -FilePath (Join-Path $toolchain "turdus_merula.exe") -ArgumentList @("--help") -Name "turdus-help"
    foreach ($operation in @("get-shcblock", "get-pteblock", "load-shcblock")) {
        if (-not $turdusHelp.Output.Contains($operation, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "turdus_merula.exe help output is missing required operation '$operation'. Exit code: $($turdusHelp.ExitCode)"
        }
        Write-Status "OK turdus help operation $operation"
    }

    Assert-BinaryString "toolchain\darksword-pongo.exe" "Exactly one PongoOS device"
    Assert-BinaryString "toolchain\darksword-pongo.exe" "multiple PongoOS devices"
    Assert-BinaryString "toolchain\darksword-pongo.exe" "sep pwn_pte"
    Assert-BinaryString "toolchain\darksword-pongo.exe" "bootux"
    Assert-BinaryString "toolchain\openra1n.exe" "openra1n-core.exe"
    Assert-BinaryString "toolchain\openra1n.exe" "PongoOS USB 05AC:4141"
    Assert-BinaryString "toolchain\openra1n.exe" "DFU remains owned by Windows"
    Assert-BinaryDoesNotContain "toolchain\openra1n.exe" "windows\palera1n.ps1"
    Assert-BinaryDoesNotContain "toolchain\openra1n.exe" "wdi-simple.exe"
    Assert-BinaryString "toolchain\openra1n-core.exe" "--pwned-dfu-only"
    Assert-BinaryString "toolchain\openra1n-core.exe" "PWND:[yolo]"
    Assert-BinaryString "toolchain\openra1n-core.exe" "Pwned DFU ready"
    Assert-BinaryDoesNotContain "toolchain\openra1n-core.exe" "YOLO:checkra1n"

    $coreUsage = Invoke-CapturedProcess -FilePath (Join-Path $toolchain "openra1n-core.exe") -ArgumentList @("--invalid-smoke-option") -Name "openra1n-core-usage"
    if ($coreUsage.ExitCode -eq 0 -or -not $coreUsage.Output.Contains("--pwned-dfu-only", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "openra1n-core.exe did not expose the separate pwned-DFU-only mode. Exit code: $($coreUsage.ExitCode)"
    }
    Write-Status "OK native pwned-DFU-only mode rejects invalid arguments before USB access"

    $nativeManifest = Get-Content (Join-Path $toolchain "native-build-manifest.txt") -Raw
    foreach ($token in @(
        "60a39f36d719344360ec2e87563ed43f61f0530f",
        "84e47176fee2d856c81f87f2caaa7aca2df679ae",
        "c2ad454aecc3354f3b1a15dcb4d4b4dc0e83b743",
        "4595a5333e4134ade77b43fb2259e880b85801ee",
        "libfragmentzip-version="
    )) {
        if (-not $nativeManifest.Contains($token, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Native build manifest is missing pinned input: $token"
        }
        Write-Status "OK pinned native input $token"
    }

    $manifestPath = Join-Path $PackageRoot "manifest.json"
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest -or $manifest.Count -lt 20) { throw "Package manifest is empty or incomplete." }
    $requiredManifestPaths = @(
        'Palera1nWin.exe', 'toolchain/openra1n.exe', 'toolchain/openra1n-core.exe',
        'toolchain/turdus_merula.exe', 'toolchain/darksword-pongo.exe', 'toolchain/wdi-simple.exe',
        'toolchain/ideviceinfo.exe', 'toolchain/irecovery.exe', 'toolchain/libusb-1.0.dll',
        'toolchain/resources/sep_racer.bin', 'toolchain/resources/kpf.bin', 'toolchain/palera1n.cmd',
        'toolchain/windows/palera1n.ps1', 'toolchain/build/fake-checkra1n.sh',
        'toolchain/build/provision-wsl.sh', 'toolchain/dist/palera1n-linux-x86_64'
    )
    $manifestPaths = @($manifest | ForEach-Object { ([string]$_.path).Replace('\','/') })
    foreach ($required in $requiredManifestPaths) {
        if ($manifestPaths -notcontains $required) { throw "Critical runtime is absent from manifest: $required" }
    }
    foreach ($entry in $manifest) {
        $relative = ([string]$entry.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $path = Join-Path $PackageRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest references a missing file: $($entry.path)" }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne [long]$entry.size) { throw "Manifest size mismatch: $($entry.path)" }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Manifest SHA-256 mismatch: $($entry.path)" }
    }
    Write-Status "OK manifest verified $($manifest.Count) files including complete Jailbreak/WSL runtime"

    $uiResultPath = Join-Path $PackageRoot "downgrade-ui-self-test-result.txt"
    Remove-Item -LiteralPath $uiResultPath -Force -ErrorAction SilentlyContinue
    $uiSmoke = Invoke-BoundedCapturedProcess `
        -FilePath (Join-Path $PackageRoot "Palera1nWin.exe") `
        -ArgumentList @("--downgrade-ui-self-test") `
        -Name "downgrade-ui-self-test" `
        -TimeoutMilliseconds 30000
    $uiResult = if (Test-Path -LiteralPath $uiResultPath) {
        Get-Content -LiteralPath $uiResultPath -Raw
    } else {
        "result file missing"
    }
    if ($uiSmoke.ExitCode -ne 0 -or -not $uiResult.StartsWith("PASS:", [System.StringComparison]::Ordinal)) {
        throw "Downgrade tab UI self-test failed with exit $($uiSmoke.ExitCode). $uiResult $($uiSmoke.Output)"
    }
    Write-Status "OK real Downgrade tab Loaded/log/timer/dashboard UI smoke test"
    Remove-Item -LiteralPath $uiResultPath -Force -ErrorAction SilentlyContinue

    $checksumPath = Join-Path $releaseRoot "SHA256SUMS.txt"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw "Release checksum file is missing." }
    $checksumLine = (Get-Content -LiteralPath $checksumPath | Select-Object -First 1).Trim()
    if ($checksumLine -notmatch '^([0-9a-fA-F]{64})\s+(.+)$') { throw "Release checksum file has an invalid format." }
    $expectedZipHash = $Matches[1].ToLowerInvariant()
    $zipName = $Matches[2].Trim()
    $zipPath = Join-Path $releaseRoot $zipName
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "Release ZIP referenced by SHA256SUMS.txt is missing: $zipName" }
    $actualZipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualZipHash -ne $expectedZipHash) { throw "Release ZIP SHA-256 mismatch." }
    Write-Status "OK release ZIP SHA-256 $actualZipHash"
    Write-Status "Complete packaged-runtime smoke test passed. Physical DFU, restore, cold boot, jailbreak, and disabled combined-plan testing remain required."
}
catch {
    Write-Status "FAILED: $($_.Exception.Message)"
    Write-Status $_.ScriptStackTrace
    throw
}
