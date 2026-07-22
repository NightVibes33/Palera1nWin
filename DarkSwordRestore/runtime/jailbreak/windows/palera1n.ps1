[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Stage([string]$Message) {
    [Console]::Out.WriteLine("[Palera1nWin] $Message")
    [Console]::Out.Flush()
}

function Quote-Bash([string]$Value) {
    return "'" + $Value.Replace("'", "'\"'\"'") + "'"
}

function Convert-ToWslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -match '^([A-Za-z]):[\\/](.*)$') {
        return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
    }
    throw "The runtime path must be on a local drive: $full"
}

function Invoke-Usbipd([string[]]$Arguments, [switch]$IgnoreExit) {
    $output = & $script:Usbipd @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $code = $LASTEXITCODE
    if ($output) { $output | ForEach-Object { Write-Stage $_ } }
    if (-not $IgnoreExit -and $code -ne 0) {
        throw "usbipd $($Arguments -join ' ') failed with exit code $code."
    }
    return ,$output
}

function Get-AppleUsbipdRows {
    $lines = & $script:Usbipd list 2>&1 | ForEach-Object { $_.ToString() }
    $rows = @()
    foreach ($line in $lines) {
        if ($line -match '^\s*(?<bus>\d+-\d+(?:\.\d+)*)\s+(?<vidpid>05ac:[0-9a-f]{4})\b') {
            $rows += [pscustomobject]@{ BusId = $Matches.bus; VidPid = $Matches.vidpid; Raw = $line }
        }
    }
    return ,$rows
}

function Require-SelectedBus([string]$RequestedBus) {
    $rows = @(Get-AppleUsbipdRows)
    if ($RequestedBus) {
        $match = @($rows | Where-Object { $_.BusId -eq $RequestedBus })
        if ($match.Count -ne 1) { throw "Selected Apple USB bus '$RequestedBus' is not present." }
        return $match[0]
    }
    if ($rows.Count -ne 1) {
        throw "Exactly one Apple USB device must be connected; usbipd reports $($rows.Count)."
    }
    return $rows[0]
}

$arguments = [Collections.Generic.List[string]]::new()
if ($RemainingArgs) { foreach ($value in $RemainingArgs) { $arguments.Add($value) } }
if ($arguments.Count -gt 0 -and $arguments[0] -eq '--') { $arguments.RemoveAt(0) }

$distro = 'Ubuntu'
$busId = $null
$skipAttach = $false
$keepShared = $false
$guiDfuPrompt = $false
$selfTest = $false
$palera1nArgs = [Collections.Generic.List[string]]::new()

for ($index = 0; $index -lt $arguments.Count; $index++) {
    $argument = $arguments[$index]
    switch ($argument) {
        '--distro' {
            if (++$index -ge $arguments.Count) { throw '--distro requires a value.' }
            $distro = $arguments[$index]
        }
        '--busid' {
            if (++$index -ge $arguments.Count) { throw '--busid requires a value.' }
            $busId = $arguments[$index]
        }
        '--no-attach' { $skipAttach = $true }
        '--keep-shared' { $keepShared = $true }
        '--gui-dfu-prompt' { $guiDfuPrompt = $true }
        '--yes' { }
        '--self-test' { $selfTest = $true }
        default { $palera1nArgs.Add($argument) }
    }
}

if ($distro -match '[\x00-\x1f]') { throw 'Invalid WSL distro name.' }
$toolchain = Split-Path -Parent $PSScriptRoot
$provisionScript = Join-Path $toolchain 'build\provision-wsl.sh'
$fakeCheckra1n = Join-Path $toolchain 'build\fake-checkra1n.sh'
$bundledBinary = Join-Path $toolchain 'dist\palera1n-linux-x86_64'

if ($selfTest) {
    foreach ($required in @($provisionScript, $fakeCheckra1n, $bundledBinary)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing packaged runtime file: $required" }
    }
    Write-Stage 'SELF-TEST OK: launcher, WSL provisioner, compatibility shim, and Linux binary are packaged.'
    exit 0
}

$script:Usbipd = (Get-Command usbipd.exe -ErrorAction SilentlyContinue).Source
if (-not $skipAttach -and -not $script:Usbipd) { throw 'usbipd-win is not installed or not on PATH.' }

$selected = $null
$temporaryScript = $null
try {
    if (-not $skipAttach) {
        $selected = Require-SelectedBus $busId
        $busId = $selected.BusId
        Write-Stage "Selected Apple USB $($selected.BusId) $($selected.VidPid)."
        Invoke-Usbipd @('bind', '--busid', $selected.BusId, '--force') -IgnoreExit | Out-Null
        Invoke-Usbipd @('attach', '--wsl', $distro, '--busid', $selected.BusId) | Out-Null
    }

    & wsl.exe -d $distro -u root -- test -x /opt/palera1n/pln-run.sh
    if ($LASTEXITCODE -ne 0) { throw "palera1n runtime is not provisioned in WSL '$distro'. Use Setup > Provision WSL." }

    $runtime = if ($env:PALERA1NWIN_RUNTIME) { $env:PALERA1NWIN_RUNTIME } else { Join-Path $env:LOCALAPPDATA 'Palera1nWin\runtime' }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $temporaryScript = Join-Path $runtime ("palera1n-run-{0}.sh" -f [Guid]::NewGuid().ToString('N'))
    $shellArguments = ($palera1nArgs | ForEach-Object { Quote-Bash $_ }) -join ' '
    $shell = "#!/usr/bin/env bash`nset -Eeuo pipefail`nexec /opt/palera1n/pln-run.sh $shellArguments`n"
    [IO.File]::WriteAllText($temporaryScript, $shell, [Text.UTF8Encoding]::new($false))
    $wslScript = Convert-ToWslPath $temporaryScript

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'wsl.exe'
    $start.Arguments = '-d "' + $distro.Replace('"', '') + '" -u root -- bash "' + $wslScript.Replace('"', '') + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $process.EnableRaisingEvents = $true
    $promptPending = 0
    $process.add_OutputDataReceived({
        param($sender, $event)
        if ($null -eq $event.Data) { return }
        [Console]::Out.WriteLine($event.Data)
        [Console]::Out.Flush()
        if ($guiDfuPrompt -and $event.Data.IndexOf('Press Enter when ready for DFU mode', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            [Threading.Interlocked]::Exchange([ref]$promptPending, 1) | Out-Null
        }
    })
    $process.add_ErrorDataReceived({
        param($sender, $event)
        if ($null -eq $event.Data) { return }
        [Console]::Error.WriteLine($event.Data)
        [Console]::Error.Flush()
        if ($guiDfuPrompt -and $event.Data.IndexOf('Press Enter when ready for DFU mode', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            [Threading.Interlocked]::Exchange([ref]$promptPending, 1) | Out-Null
        }
    })

    if (-not $process.Start()) { throw 'Could not start the WSL palera1n process.' }
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $signal = Join-Path $runtime 'dfu-enter.signal'
    $promptDeadline = $null

    while (-not $process.HasExited) {
        if ($guiDfuPrompt -and [Threading.Volatile]::Read([ref]$promptPending) -eq 1) {
            if (-not $promptDeadline) { $promptDeadline = [DateTimeOffset]::UtcNow.AddMinutes(3) }
            if (Test-Path -LiteralPath $signal) {
                Remove-Item -LiteralPath $signal -Force -ErrorAction SilentlyContinue
                $process.StandardInput.WriteLine()
                $process.StandardInput.Flush()
                [Threading.Interlocked]::Exchange([ref]$promptPending, 0) | Out-Null
                $promptDeadline = $null
                Write-Stage 'GUI DFU confirmation delivered to palera1n.'
            } elseif ([DateTimeOffset]::UtcNow -gt $promptDeadline) {
                try { $process.Kill() } catch { }
                throw 'Timed out waiting for the GUI DFU confirmation signal.'
            }
        }
        Start-Sleep -Milliseconds 100
    }

    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    exit $exitCode
}
finally {
    if ($temporaryScript) { Remove-Item -LiteralPath $temporaryScript -Force -ErrorAction SilentlyContinue }
    if (-not $skipAttach -and -not $keepShared -and $selected -and $script:Usbipd) {
        Invoke-Usbipd @('detach', '--busid', $selected.BusId) -IgnoreExit | Out-Null
        Invoke-Usbipd @('unbind', '--busid', $selected.BusId) -IgnoreExit | Out-Null
    }
}
