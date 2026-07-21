[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot
)

$ErrorActionPreference = "Stop"
$toolchain = Join-Path $PackageRoot "toolchain"
$logDirectory = Join-Path $PackageRoot "smoke-logs"
New-Item -ItemType Directory -Force $logDirectory | Out-Null

$required = @(
    "turdus_merula.exe",
    "openra1n.exe",
    "openra1n-core.exe",
    "darksword-pongo.exe",
    "wdi-simple.exe",
    "libusb-1.0.dll",
    "native-build-manifest.txt",
    "resources\sep_racer.bin",
    "resources\kpf.bin"
)
foreach ($relative in $required) {
    $path = Join-Path $toolchain $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing packaged component: $relative"
    }
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
    [PSCustomObject]@{
        ExitCode = $process.ExitCode
        Output = ($output -join [Environment]::NewLine)
    }
}

$turdus = Invoke-CapturedProcess `
    -FilePath (Join-Path $toolchain "turdus_merula.exe") `
    -ArgumentList @("--help") `
    -Name "turdus-help"
if ($turdus.Output -notmatch "get-shcblock" -or $turdus.Output -notmatch "get-pteblock") {
    throw "turdus_merula.exe help output is missing required turdus operations. Exit code: $($turdus.ExitCode)"
}

$pongo = Invoke-CapturedProcess `
    -FilePath (Join-Path $toolchain "darksword-pongo.exe") `
    -Name "pongo-usage"
if ($pongo.ExitCode -ne 1) {
    throw "Unexpected darksword-pongo usage exit code: $($pongo.ExitCode)"
}

Write-Host "Package smoke test passed."
Write-Host "turdus libirecovery transport: statically linked"
