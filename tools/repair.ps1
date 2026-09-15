<#
.SYNOPSIS
    Diagnoses and fixes the two things that stop tia-cli on a fresh machine:
    a daemon that will not launch, and an Openness approval dialog that never appears.

.DESCRIPTION
    install.ps1 already clears the mark of the web and any forced-elevation flag, so a normal
    install should not need this. It is here for what install.ps1 cannot reach without
    administrator rights - chiefly writing the Openness whitelist entry itself - and for
    diagnosing a machine where something else is in the way.

    Runs six checks, each of which prints what it found before it changes anything:

      1. Environment      - 64-bit PowerShell, elevated, exe present
      2. Mark of the Web  - unblocks the downloaded binaries
      3. AppCompatFlags   - clears a stray RUNASADMIN layer on tia.exe
      4. Openness group    - reports membership, and whether your logon token has it yet
      5. Whitelist         - writes the HKLM approval entry for this exact tia.exe
      6. Verify            - reads the entry back the same way OpennessWhitelist.cs does

    The whitelist entry records the exe's full path and a base64 SHA-256 of its bytes.
    Both must match at runtime, so run this AFTER install.ps1 has copied tia.exe to its
    final home, and run it again after any upgrade that replaces the exe.

.PARAMETER ExePath
    Full path to the installed tia.exe. Defaults to the install.ps1 location.

.PARAMETER OpennessVersion
    Which Openness version key to write under. Defaults to every version installed.

.PARAMETER DryRun
    Report only. Makes no changes.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\repair.ps1 -DryRun
    powershell -ExecutionPolicy Bypass -File .\repair.ps1
#>

[CmdletBinding()]
param(
    [string]   $ExePath = (Join-Path $env:LOCALAPPDATA 'Programs\tia-cli\tia.exe'),
    [string[]] $OpennessVersion,
    [switch]   $DryRun
)

$ErrorActionPreference = 'Stop'
$script:Failed = $false

function Say  ($m) { Write-Host "  $m" }
function Ok   ($m) { Write-Host "  [ ok ] $m"   -ForegroundColor Green }
function Warn ($m) { Write-Host "  [warn] $m"   -ForegroundColor Yellow }
function Fail ($m) { Write-Host "  [FAIL] $m"   -ForegroundColor Red; $script:Failed = $true }
function Step ($n) { Write-Host "`n== $n" -ForegroundColor Cyan }

# --- 1. Environment ----------------------------------------------------------
Step '1. Environment'

if (-not [Environment]::Is64BitProcess) {
    Fail 'This is 32-bit PowerShell. tia-cli reads the 64-bit registry view, so an entry'
    Say  '       written here would land under Wow6432Node and never be found.'
    Say  '       Re-run from C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    exit 1
}
Ok '64-bit PowerShell'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Fail 'Not elevated. The whitelist lives under HKLM and needs administrator rights.'
    Say  '       Right-click PowerShell -> Run as administrator, then re-run this script.'
    exit 1
}
Ok 'Running elevated'

if (-not (Test-Path -LiteralPath $ExePath)) {
    Fail "tia.exe not found at: $ExePath"
    Say  '       If UAC elevated you into a DIFFERENT admin account, $env:LOCALAPPDATA now'
    Say  '       points at that account, not yours. Pass the real path explicitly:'
    Say  '         -ExePath "C:\Users\<you>\AppData\Local\Programs\tia-cli\tia.exe"'
    exit 1
}

$exe     = Get-Item -LiteralPath $ExePath
$exeDir  = $exe.DirectoryName
$fullPath = $exe.FullName
Ok "Found $fullPath"
Say "       built $($exe.LastWriteTime), $($exe.Length) bytes"

# --- 2. Mark of the Web ------------------------------------------------------
# ERROR_CANCELLED (1223) out of ShellExecute is usually SmartScreen on an unsigned,
# internet-downloaded exe. The zone tag is an alternate data stream, so clearing it
# does not change the file hash computed below.
Step '2. Mark of the Web'

$blocked = Get-ChildItem -LiteralPath $exeDir -File |
    Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }

if ($blocked) {
    Warn "$($blocked.Count) file(s) still tagged as downloaded from the internet:"
    $blocked | ForEach-Object { Say "       $($_.Name)" }
    if (-not $DryRun) {
        $blocked | Unblock-File
        Ok 'Unblocked'
    }
} else {
    Ok 'No zone tags'
}

# --- 3. Stray elevation flag -------------------------------------------------
# A RUNASADMIN layer makes the hidden ShellExecute in DaemonClient.Start raise a UAC
# prompt with nowhere to show it. Windows answers on your behalf: error 1223.
Step '3. Compatibility flags'

$layerKeys = @(
    'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
    'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
)

$foundLayer = $false
foreach ($lk in $layerKeys) {
    if (-not (Test-Path $lk)) { continue }
    $props = Get-ItemProperty -Path $lk
    foreach ($name in $props.PSObject.Properties.Name) {
        if ($name -notlike '*tia.exe') { continue }
        $value = $props.$name
        if ($value -notmatch 'RUNASADMIN') { continue }

        $foundLayer = $true
        Warn "RUNASADMIN set on $name"
        Say  "       in $lk"
        if (-not $DryRun) {
            Remove-ItemProperty -Path $lk -Name $name
            Ok 'Removed'
        }
    }
}
if (-not $foundLayer) { Ok 'No forced-elevation layer on tia.exe' }

if ($env:USERNAME -and $fullPath -notlike "*$env:USERNAME*") {
    Warn 'HKCU here belongs to the elevating account. If UAC switched users, check the'
    Say  '       HKCU Layers key under your OWN account as well.'
}

# --- 4. Openness group -------------------------------------------------------
# Membership is read at logon. Being in the group but not in your token is the classic
# "I added myself and it still says access_denied" case.
Step '4. Siemens TIA Openness group'

$groupName = 'Siemens TIA Openness'
try {
    $members = (Get-LocalGroupMember -Group $groupName -ErrorAction Stop).Name
    $me      = "$env:USERDOMAIN\$env:USERNAME"
    $inGroup = $members -contains $me

    $inToken = (whoami /groups) -match [regex]::Escape($groupName)

    if     ($inGroup -and $inToken) { Ok "$me is a member, and the token has it" }
    elseif ($inGroup)               { Warn "$me is a member but your logon token predates it. Sign out and back in." }
    else {
        Fail "$me is NOT in '$groupName'. Every Openness call will fail with access_denied (exit 7)."
        Say  "       Add-LocalGroupMember -Group '$groupName' -Member '$me'   then sign out and back in."
    }
} catch {
    Fail "Local group '$groupName' does not exist."
    Say  '       TIA Portal was installed without the Openness option. Re-run the Siemens'
    Say  '       installer and select it. Nothing below will help until that is fixed.'
}

# --- 5. Whitelist entry ------------------------------------------------------
Step '5. Openness whitelist'

$root = 'HKLM:\SOFTWARE\Siemens\Automation\Openness'
if (-not (Test-Path $root)) {
    Fail "$root does not exist - no Openness installation detected."
    exit 1
}

$versions = if ($OpennessVersion) { $OpennessVersion }
            else { (Get-ChildItem $root | Select-Object -ExpandProperty PSChildName) }

if (-not $versions) { Fail "No version subkeys under $root"; exit 1 }
Say "Versions installed: $($versions -join ', ')"

# Base64 SHA-256 over the file's default data stream - exactly what OpennessWhitelist.HashOf does.
$sha  = [Security.Cryptography.SHA256]::Create()
$fs   = [IO.File]::OpenRead($fullPath)
try   { $hash = [Convert]::ToBase64String($sha.ComputeHash($fs)) }
finally { $fs.Dispose(); $sha.Dispose() }

Say "FileHash: $hash"
$stamp = $exe.LastWriteTimeUtc.ToString("yyyy'/'MM'/'dd HH:mm:ss.fff")

foreach ($v in $versions) {
    $key = Join-Path $root "$v\Whitelist\$($exe.Name)\Entry"
    if ($DryRun) { Say "would write $key"; continue }

    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name 'Path'         -Value $fullPath
    Set-ItemProperty -Path $key -Name 'FileHash'     -Value $hash
    Set-ItemProperty -Path $key -Name 'DateModified' -Value $stamp
    Ok "Wrote $key"
}

# --- 6. Verify ---------------------------------------------------------------
# Mirrors OpennessWhitelist.IsApproved: walk every version, every entry subkey, and
# match Path case-insensitively against FileHash exactly.
Step '6. Verify'

if ($DryRun) {
    Say 'Skipped (dry run).'
} else {
    $approved = $false
    foreach ($v in (Get-ChildItem $root | Select-Object -ExpandProperty PSChildName)) {
        $wl = Join-Path $root "$v\Whitelist\$($exe.Name)"
        if (-not (Test-Path $wl)) { continue }
        foreach ($entry in (Get-ChildItem $wl -ErrorAction SilentlyContinue)) {
            $p = (Get-ItemProperty $entry.PSPath -Name Path     -ErrorAction SilentlyContinue).Path
            $h = (Get-ItemProperty $entry.PSPath -Name FileHash -ErrorAction SilentlyContinue).FileHash
            if ($p -and $h -and ($p -ieq $fullPath) -and ($h -ceq $hash)) {
                $approved = $true
                Ok "Approved under Openness $v"
            }
        }
    }
    if (-not $approved) { Fail 'Entry did not read back. Check for a redirected registry view.' }
}

# --- Summary -----------------------------------------------------------------
Write-Host ''
if ($script:Failed) {
    Write-Host 'Finished with problems - see [FAIL] lines above.' -ForegroundColor Red
} else {
    Write-Host 'Done.' -ForegroundColor Green
}

Write-Host @"

Next steps
  1. Open a NEW terminal (PATH and any group change need a fresh one).
  2. Open TIA Portal normally, with its window on screen.
  3. Run:  tia devices --no-daemon
     Foreground, against a visible portal. If an "Openness access" window still
     appears, answer it once - the entry above only pre-empts it, and TIA Portal
     has the final say.
  4. Only once that works:  tia session start

If the daemon still will not start
  Run  tia --serve  in a visible terminal and use tia normally from a second one.
  Whatever ShellExecute is refusing will be visible there instead of swallowed.
  Also read:  $env:LOCALAPPDATA\tia-cli\daemon.log

Remember
  Approval follows the bytes. Any upgrade that replaces tia.exe invalidates this
  entry - re-run the script after it.
"@ -ForegroundColor Gray
