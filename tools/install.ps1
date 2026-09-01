<#
.SYNOPSIS
    Installs tia-cli for the current user.

.DESCRIPTION
    Copies the tool to a stable folder and puts it on your PATH. No administrator rights are
    needed: everything lands under your own profile and only HKCU is written.

    The install folder matters more than it looks. TIA Portal's Openness whitelist records the
    approved exe's path *and* a hash of its bytes, so a tool that moves between runs asks for
    approval again every time. Installing to one fixed place and leaving it there is the point.

.PARAMETER InstallDir
    Where to install. Defaults to %LOCALAPPDATA%\Programs\tia-cli.

.PARAMETER NoPath
    Skip the PATH change. You will have to call tia.exe by its full path.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\tia-cli'),
    [switch]$NoPath
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot

function Write-Step($text) { Write-Host "  $text" }
function Write-Warn($text) { Write-Host "  ! $text" -ForegroundColor Yellow }

# The user PATH must be read and written unexpanded. Reading it through the normal environment API
# expands %USERPROFILE% and friends, and writing that back would silently freeze those entries.
function Get-UserPathRaw {
    $key = Get-Item 'HKCU:\Environment'
    [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

function Set-UserPathRaw([string]$value) {
    $kind = if ($value -match '%') { 'ExpandString' } else { 'String' }
    Set-ItemProperty -Path 'HKCU:\Environment' -Name Path -Value $value -Type $kind
    # Without this broadcast, nothing started from Explorer sees the new PATH until you sign out.
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

Write-Host ''
Write-Host 'Installing tia-cli'
Write-Host ''

# ---------------------------------------------------------------- checks
Write-Host 'Checking this machine'

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Openness is 64-bit only, and this is a 32-bit Windows.'
}

$ndp = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if (-not $ndp -or $ndp.Release -lt 528040) {
    throw '.NET Framework 4.8 is required and was not found. It ships with current Windows; install it and run this again.'
}
Write-Step '.NET Framework 4.8 present'

$openness = Get-ChildItem 'HKLM:\SOFTWARE\Siemens\Automation\Openness' -ErrorAction SilentlyContinue
if ($openness) {
    Write-Step ("TIA Portal Openness present (" + (($openness.PSChildName | Sort-Object) -join ', ') + ')')
} else {
    Write-Warn 'No TIA Portal Openness installation found. tia installs fine, but every command will'
    Write-Warn 'fail until TIA Portal is installed with the Openness option.'
}

# Membership is read at logon, so a freshly added account still fails until it signs out and back in.
$inGroup = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    foreach ($group in $identity.Groups) {
        try {
            if ($group.Translate([Security.Principal.NTAccount]).Value -match 'Siemens TIA Openness') {
                $inGroup = $true
                break
            }
        } catch { }
    }
} catch { }

if ($inGroup) {
    Write-Step 'Your account is in the Siemens TIA Openness group'
} else {
    Write-Warn 'Your account is not in the local "Siemens TIA Openness" group, so Openness calls will'
    Write-Warn 'fail with access_denied. Add it, then sign out and back in - membership is read at logon.'
}

$exeSource = Join-Path $source 'tia.exe'
if (-not (Test-Path $exeSource)) {
    throw "tia.exe is not next to this script. Run install.ps1 from the folder you unpacked the zip into."
}

# ---------------------------------------------------------------- stop anything running
$existing = Join-Path $InstallDir 'tia.exe'
if (Test-Path $existing) {
    Write-Host ''
    Write-Host 'Replacing an existing install'
    # A running daemon holds tia.exe open and keeps a TIA Portal alive; both have to go first.
    try {
        & $existing session stop 2>&1 | Out-Null
        Write-Step 'Stopped the running session'
    } catch { }

    Get-Process -Name 'tia' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, 'OrdinalIgnoreCase') } |
        ForEach-Object {
            Write-Step "Stopping leftover process $($_.Id)"
            try { $_.Kill(); $_.WaitForExit(5000) } catch { }
        }
}

# ---------------------------------------------------------------- copy
Write-Host ''
Write-Host "Installing to $InstallDir"

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

$payload = Get-ChildItem -Path $source -File |
    Where-Object { $_.Name -ne 'install.ps1' }

foreach ($file in $payload) {
    Copy-Item -Path $file.FullName -Destination (Join-Path $InstallDir $file.Name) -Force
}
Write-Step "Copied $($payload.Count) files"

$exe = Join-Path $InstallDir 'tia.exe'
if (-not (Test-Path $exe)) { throw "Install failed: $exe does not exist." }

# ---------------------------------------------------------------- PATH
if (-not $NoPath) {
    $raw = Get-UserPathRaw
    $entries = $raw -split ';' | Where-Object { $_ -ne '' }
    $already = $entries | Where-Object { $_.TrimEnd('\') -ieq $InstallDir.TrimEnd('\') }

    if ($already) {
        Write-Step 'Already on your PATH'
    } else {
        $updated = (@($entries) + $InstallDir) -join ';'
        Set-UserPathRaw $updated
        Write-Step 'Added to your user PATH'
    }
}

# ---------------------------------------------------------------- done
# ProductVersion carries a "+<commit sha>" suffix once the tree is a git repository.
$version = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
Write-Host ''
Write-Host "Installed tia $version" -ForegroundColor Green
Write-Host ''
Write-Host 'Next:'
if (-not $NoPath) {
    Write-Host '  Open a new terminal (this one has the old PATH), then run: tia help'
} else {
    Write-Host "  Run: $exe help"
}
Write-Host ''
Write-Host '  The first Openness command shows a TIA Portal "Openness access" window and waits until'
Write-Host '  you answer it. Approve it once; the approval covers this installed copy and is remembered'
Write-Host '  until tia.exe is replaced by a new version.'
Write-Host ''
Write-Host "  To remove: powershell -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
Write-Host ''
