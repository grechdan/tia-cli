<#
.SYNOPSIS
    Removes tia-cli.

.DESCRIPTION
    Stops any running session, takes the tool off your PATH and deletes it. Run it from the folder
    tia was installed into - install.ps1 leaves a copy of this script there.

    Stopping the session first is not a nicety. A background session owns the TIA Portal it started,
    and deleting the tool without stopping it leaves that portal running with nothing left to
    close it.

.PARAMETER InstallDir
    The folder to remove. Defaults to the folder this script is in.

.PARAMETER KeepData
    Leave %LOCALAPPDATA%\tia-cli (the daemon state file and log) in place.

.PARAMETER RemoveWhitelist
    Also drop this install's entries from TIA Portal's Openness whitelist under HKLM. Needs an
    elevated prompt; without it the entry is harmless and simply goes unused.

.PARAMETER Yes
    Do not ask for confirmation.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$KeepData,
    [switch]$RemoveWhitelist,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside a param() default under Windows PowerShell 5.1, so the fallback to
# "the folder this script sits in" has to happen here instead.
if (-not $InstallDir) { $InstallDir = $PSScriptRoot }
if (-not $InstallDir) { $InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $InstallDir) { throw 'Could not work out which folder to remove. Pass -InstallDir.' }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)

function Write-Step($text) { Write-Host "  $text" }
function Write-Warn($text) { Write-Host "  ! $text" -ForegroundColor Yellow }

function Get-UserPathRaw {
    $key = Get-Item 'HKCU:\Environment'
    [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

function Set-UserPathRaw([string]$value) {
    $kind = if ($value -match '%') { 'ExpandString' } else { 'String' }
    Set-ItemProperty -Path 'HKCU:\Environment' -Name Path -Value $value -Type $kind
    if (-not ('Win32.NativeMethods' -as [type])) {
        Add-Type -Namespace Win32 -Name NativeMethods -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam,
    string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }
    $result = [UIntPtr]::Zero
    [void][Win32.NativeMethods]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [UIntPtr]::Zero,
        'Environment', 2, 5000, [ref]$result)
}

$exe = Join-Path $InstallDir 'tia.exe'

Write-Host ''
Write-Host "Removing tia-cli from $InstallDir"
Write-Host ''

if (-not (Test-Path $exe)) {
    Write-Warn "No tia.exe in $InstallDir - nothing to remove there."
    Write-Warn 'Pass -InstallDir if tia is installed somewhere else.'
    if (-not $Yes) { return }
}

if (-not $Yes) {
    $answer = Read-Host 'This stops any running session and deletes the tool. Continue? [y/N]'
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host 'Nothing was changed.'
        return
    }
    Write-Host ''
}

# ---------------------------------------------------------------- stop the session
if (Test-Path $exe) {
    try {
        $status = & $exe session stop 2>&1 | Out-String
        if ($status -match 'Session stopped') {
            Write-Step 'Stopped the running session'
            if ($status -match 'closing') { Write-Step 'Its TIA Portal was closed with it' }
        } else {
            Write-Step 'No session was running'
        }
    } catch {
        Write-Warn "Could not ask tia to stop its session: $($_.Exception.Message)"
    }
}

Get-Process -Name 'tia' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, 'OrdinalIgnoreCase') } |
    ForEach-Object {
        Write-Step "Stopping leftover process $($_.Id)"
        try { $_.Kill(); $_.WaitForExit(5000) } catch { }
    }

# ---------------------------------------------------------------- PATH
$raw = Get-UserPathRaw
$entries = $raw -split ';' | Where-Object { $_ -ne '' }
$kept = $entries | Where-Object { $_.TrimEnd('\') -ine $InstallDir.TrimEnd('\') }

if ($kept.Count -ne $entries.Count) {
    Set-UserPathRaw ($kept -join ';')
    Write-Step 'Removed from your user PATH'
} else {
    Write-Step 'Was not on your PATH'
}

# ---------------------------------------------------------------- data
if ($KeepData) {
    Write-Step 'Kept the daemon state file and log'
} else {
    $data = Join-Path $env:LOCALAPPDATA 'tia-cli'
    if (Test-Path $data) {
        try {
            Remove-Item -Path $data -Recurse -Force
            Write-Step 'Removed the daemon state file and log'
        } catch {
            Write-Warn "Could not remove $data - $($_.Exception.Message)"
        }
    }
}

# ---------------------------------------------------------------- whitelist
if ($RemoveWhitelist) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $admin) {
        Write-Warn 'The Openness whitelist lives under HKLM and needs an elevated prompt. Skipped.'
    } else {
        $removed = 0
        $root = 'HKLM:\SOFTWARE\Siemens\Automation\Openness'
        foreach ($version in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            $entriesKey = Join-Path $version.PSPath 'Whitelist\tia.exe'
            foreach ($entry in (Get-ChildItem $entriesKey -ErrorAction SilentlyContinue)) {
                $path = (Get-ItemProperty $entry.PSPath -Name Path -ErrorAction SilentlyContinue).Path
                if ($path -and $path.StartsWith($InstallDir, 'OrdinalIgnoreCase')) {
                    Remove-Item $entry.PSPath -Recurse -Force
                    $removed++
                }
            }
        }
        Write-Step "Removed $removed Openness whitelist entr$(if ($removed -eq 1) {'y'} else {'ies'})"
    }
}

# ---------------------------------------------------------------- files
$self = $MyInvocation.MyCommand.Path
$deferred = $false

foreach ($file in (Get-ChildItem -Path $InstallDir -File -ErrorAction SilentlyContinue)) {
    if ($self -and $file.FullName -ieq $self) { $deferred = $true; continue }
    try { Remove-Item $file.FullName -Force } catch { Write-Warn "Could not delete $($file.Name)" }
}

if ($deferred) {
    # This script cannot delete the folder it is running from, so the empty folder is handed to a
    # detached process that starts once we have exited. It retries: a virus scanner or the search
    # indexer can hold a freshly written folder for several seconds after the files inside are gone.
    Write-Step 'Removed the installed files'

    $cleanup = @"
for (`$i = 0; `$i -lt 30; `$i++) {
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath '$InstallDir' -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath '$InstallDir')) { break }
}
"@
    # EncodedCommand sidesteps every layer of command-line quoting for the path.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanup))
    Start-Process -WindowStyle Hidden -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)

    Write-Step 'The empty folder is removed a moment from now'
} else {
    try {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Step 'Removed the install folder'
    } catch { }
}

Write-Host ''
Write-Host 'tia-cli removed.' -ForegroundColor Green
if (-not $RemoveWhitelist) {
    Write-Host ''
    Write-Host '  TIA Portal still has this build in its Openness whitelist under HKLM. It is harmless'
    Write-Host '  and unused; an elevated -RemoveWhitelist run clears it.'
}
Write-Host ''
