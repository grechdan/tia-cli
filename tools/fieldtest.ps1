<#
.SYNOPSIS
    Runs the tia-cli test matrix on a machine that has the hardware, and writes a report to send back.

.DESCRIPTION
    Everything here runs against an installed tia.exe. Nothing is compiled, so the machine needs no
    development tools - PowerShell and the installed CLI are enough.

    Every verb the CLI answers has at least one case here, and a unit test (WiringTests) fails when
    a verb is added without one. What a run cannot reach on a given machine is skipped by name.

    Tests are grouped by what they need, and a group is skipped with a reason rather than failed when
    the machine cannot host it:

      core    - a TIA Portal and a licence. Always runs.
      ui      - a portal with a user interface on screen (the show verbs, attaching to it)
      plcsim  - S7-PLCSIM installed
      plc     - a real PLC reachable on the network, named with -PlcAddress

    Nothing writes to a PLC unless you pass -AllowPlcWrite as well as -PlcAddress, and even then it
    asks first. A download stops and reloads a controller: on a live machine that is a physical
    event, not a test step. Read-only PLC traffic (upload) needs only -PlcAddress.

    The report is markdown plus a JSON sidecar, holding every command, its exit code and its full
    output. Send both files back; they are the whole point of the run.

.PARAMETER ExePath
    Installed tia.exe. Defaults to the install.ps1 location.

.PARAMETER ProjectDir
    Where the throwaway test project is created. Defaults to a temp folder. Deleted afterwards
    unless -KeepProject.

.PARAMETER PlcAddress
    IP address of a PLC to test against, e.g. 192.168.0.1. Without it the plc group is skipped.

.PARAMETER AllowPlcWrite
    Permit 'tia download', which stops and reloads the controller at -PlcAddress.

.PARAMETER SkipUi
    Skip the tests that need a portal window on screen.

.PARAMETER Yes
    Do not ask for confirmation before the PLC write tests.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File fieldtest.ps1
    powershell -ExecutionPolicy Bypass -File fieldtest.ps1 -PlcAddress 192.168.0.1
    powershell -ExecutionPolicy Bypass -File fieldtest.ps1 -PlcAddress 192.168.0.1 -AllowPlcWrite
#>

[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $ProjectDir,
    [string] $PlcAddress,
    [string] $ReportDir,
    [string] $OpennessVersion,
    [switch] $AllowPlcWrite,
    [switch] $SkipUi,
    [switch] $KeepProject,
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath)   { $ExePath   = Join-Path $env:LOCALAPPDATA 'Programs\tia-cli\tia.exe' }
if (-not $ReportDir) { $ReportDir = Join-Path ([Environment]::GetFolderPath('Desktop')) 'tia-cli-fieldtest' }
if (-not $ProjectDir) {
    $ProjectDir = Join-Path ([IO.Path]::GetTempPath()) ('tia-fieldtest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}

# TIA Portal (V20, at least) refuses a project folder longer than 143 characters, and says so only
# in the daemon's log - the command itself fails with a bare "Error when calling method 'Create'".
# Checked here so a long -ProjectDir, or a long user name under %TEMP%, fails with the reason.
# The deepest project folder of the run is the one 'project retrieve' unpacks.
$RetrieveDir   = Join-Path $ProjectDir 'r'
# The workspace case retrieves deeper still, into ws\project.
$projectFolder = Join-Path $ProjectDir 'ws\project\FieldTest'
if ($projectFolder.Length -gt 143) {
    throw "The test project would end up at $projectFolder ($($projectFolder.Length) characters), and TIA Portal allows at most 143. Pass a shorter -ProjectDir, such as C:\tia-test."
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw @"
tia.exe not found at $ExePath.

Either install it first:
    powershell -ExecutionPolicy Bypass -File install.ps1

or point this at an unpacked copy:
    powershell -ExecutionPolicy Bypass -File fieldtest.ps1 -ExePath .\tia.exe
"@
}
$ExePath = (Get-Item -LiteralPath $ExePath).FullName

<#  Running straight from an unpacked zip skips install.ps1, and with it the clearing of the mark
    of the web. Left in place it stops the daemon from launching at all - ShellExecute refuses an
    unsigned, internet-tagged exe and reports it as "the operation was canceled by the user". The
    tag lives in an alternate data stream, so clearing it does not change the file's bytes and the
    Openness approval is unaffected.  #>
$zoneTagged = @(Get-ChildItem -LiteralPath (Split-Path $ExePath) -File -ErrorAction SilentlyContinue |
    Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue })

if ($zoneTagged) {
    $zoneTagged | Unblock-File
    Write-Host "  Unblocked $($zoneTagged.Count) files still marked as downloaded from the internet." -ForegroundColor Yellow
    Write-Host '  (install.ps1 does this too; running from the unpacked zip skips it.)' -ForegroundColor DarkGray
}

# The confidential-configuration password of the throwaway project: set by 'device protection',
# asked for again by every download. It protects nothing and exists nowhere else.
$Secret = 'FieldTest_1234!'

$script:Results = New-Object Collections.ArrayList
$script:Env     = [ordered]@{}

function Head ($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Note ($t) { Write-Host "   $t" -ForegroundColor DarkGray }

<#  Runs one tia command and records everything about it. Expected is a list of acceptable exit
    codes; a case whose exit code is outside that list is a finding, not a crash - the run keeps
    going, because a report covering ten things is worth more than a script that stopped at the
    second.  #>
function Case {
    param(
        [string]   $Name,
        [string]   $Group,
        [string[]] $TiaArgs,
        [int[]]    $Expect = @(0),
        [string]   $Note,
        # Text to pipe into the command, for the verbs that read stdin.
        [string]   $Stdin
    )

    # Nearly every case works on the station 'device add' makes. Without it they would all fail the
    # same way, and the one real finding would be buried under fifty echoes of it.
    if ($script:NoStation -and $TiaArgs -contains 'PLC_1') {
        Skip -Name $Name -Group $Group -Why $script:NoStation
        $script:LastPassed = $false
        return
    }

    $started = Get-Date

    # Before the session exists, tia resolves Openness in its own process, so those commands need the
    # version as well. Once 'session start' has passed, the daemon has settled it and repeating the
    # flag only earns a warning.
    if ($OpennessVersion -and -not $script:InSession -and $TiaArgs -notcontains '--openness-version') {
        $TiaArgs = @($TiaArgs) + @('--openness-version', $OpennessVersion)
    }

    # tia writes warnings to stderr - the pending-approval notice among them. Under
    # ErrorActionPreference 'Stop' PowerShell turns any stderr line from a native command into a
    # terminating NativeCommandError, which would record a warning as a failed command. Relax it
    # here so the exit code decides the verdict, which is the only thing that should.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # Kept as plain text: each stderr line arrives wrapped in an ErrorRecord, and printing those
        # adds a script-position banner to every error message in the report.
        $run = if ($Stdin) { { $Stdin | & $ExePath @TiaArgs 2>&1 } } else { { & $ExePath @TiaArgs 2>&1 } }
        $output = (& $run | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_ }
        } | Out-String)
        $code = $LASTEXITCODE
    } catch {
        $output = $_.Exception.Message
        $code   = -1
    } finally {
        $ErrorActionPreference = $previous
    }
    $elapsed = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    $pass = $Expect -contains $code
    $verdict = if ($pass) { 'pass' } else { 'FAIL' }

    if ($pass -and $TiaArgs[0] -eq 'session') {
        if ($TiaArgs[1] -eq 'start') { $script:InSession = $true }
        if ($TiaArgs[1] -eq 'stop' -and $TiaArgs -notcontains '--attach') { $script:InSession = $false }
    }

    [void]$script:Results.Add([pscustomobject]@{
        Name = $Name; Group = $Group; Command = $(if ($Stdin) { '(stdin) | ' }) + 'tia ' + ($TiaArgs -join ' ')
        Expected = ($Expect -join ' or '); Exit = $code; Verdict = $verdict
        Seconds = $elapsed; Output = $output.TrimEnd(); Note = $Note
    })

    $colour = if ($pass) { 'Green' } else { 'Red' }
    Write-Host ("   [{0}] {1}  (exit {2}, {3}s)" -f $verdict, $Name, $code, $elapsed) -ForegroundColor $colour

    # Left in a variable rather than returned: an uncaptured return value would print itself into
    # the middle of the run.
    $script:LastPassed = $pass
}

function Skip {
    param([string]$Name, [string]$Group, [string]$Why)
    [void]$script:Results.Add([pscustomobject]@{
        Name = $Name; Group = $Group; Command = ''; Expected = ''; Exit = $null
        Verdict = 'skipped'; Seconds = 0; Output = ''; Note = $Why
    })
    Write-Host "   [skip] $Name - $Why" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- environment
Head 'Environment'

$script:Env['when']     = (Get-Date).ToString('s')
$script:Env['machine']  = $env:COMPUTERNAME
$script:Env['user']     = "$env:USERDOMAIN\$env:USERNAME"
$script:Env['windows']  = (Get-CimInstance Win32_OperatingSystem).Caption + ' ' + [Environment]::OSVersion.Version
$script:Env['exe']      = $ExePath
$script:Env['exeBuilt'] = (Get-Item $ExePath).LastWriteTime.ToString('s')
$script:Env['tia']      = (& $ExePath --version 2>&1 | Out-String).Trim()

$openness = Get-ChildItem 'HKLM:\SOFTWARE\Siemens\Automation\Openness' -ErrorAction SilentlyContinue
$script:Env['openness'] = if ($openness) { ($openness.PSChildName | Sort-Object) -join ', ' } else { 'none' }

$script:Env['plcsim'] = if (Get-ChildItem 'C:\Program Files\Siemens\Automation' -Filter '*PLCSIM*' -ErrorAction SilentlyContinue) { 'present' } else { 'not found' }

try {
    $script:Env['opennessGroup'] = if ((whoami /groups) -match 'Siemens TIA Openness') { 'in token' } else { 'NOT in token' }
} catch { $script:Env['opennessGroup'] = 'unknown' }

$script:Env['plcAddress']    = if ($PlcAddress) { $PlcAddress } else { '(none given)' }
$script:Env['allowPlcWrite'] = [bool]$AllowPlcWrite

foreach ($k in $script:Env.Keys) { Note ("{0,-14} {1}" -f $k, $script:Env[$k]) }

if ($script:Env['openness'] -eq 'none') { throw 'No TIA Portal Openness installation found. Nothing can be tested here.' }

# ---------------------------------------------------------------- nobody else's session
# The harness needs a session of its own. 'session start' reports success when one is already
# running, so without this the run would quietly share it: adopting whatever project it holds,
# calling 'portals' on it - which, in 0.1.0, disposes the session's portal - and finishing with
# 'session stop', which closes a portal tia started along with anything unsaved in it.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { $sessionState = (& $ExePath session list 2>&1 | Out-String) }
finally { $ErrorActionPreference = $previous }

# Cleanup ends every session with 'session stop --all', so none may be running beforehand.
if ($sessionState -notmatch 'No tia session is running') {
    throw "A tia session is already running. The field test needs the machine to itself, and its cleanup would end yours. Save anything open in it, run 'tia session stop --all', then run this again."
}

# Portals somebody already had open. 'tia portals' used to close every portal it listed - window,
# project and unsaved work - and the run checks that these are all still there after it.
$foreignPortals = @(Get-Process -Name 'Siemens.Automation.Portal' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })

# ---------------------------------------------------------------- consent for PLC writes
$plcWriteOk = $false
if ($AllowPlcWrite) {
    if (-not $PlcAddress) { throw '-AllowPlcWrite needs -PlcAddress as well.' }
    if ($Yes) {
        $plcWriteOk = $true
    } else {
        Write-Host ''
        Write-Host "  'tia download' will STOP AND RELOAD the controller at $PlcAddress." -ForegroundColor Yellow
        Write-Host '  Only continue if that PLC is a test unit driving nothing that matters.' -ForegroundColor Yellow
        $plcWriteOk = (Read-Host '  Type the PLC address again to confirm') -eq $PlcAddress
        if (-not $plcWriteOk) { Write-Host '  Not confirmed - PLC write tests will be skipped.' -ForegroundColor Yellow }
    }
}

# ---------------------------------------------------------------- session
Head 'Session'

Case -Name 'version'                      -Group core -TiaArgs @('--version')
Case -Name 'help'                         -Group core -TiaArgs @('help')
Case -Name 'unknown verb exits 2'         -Group core -TiaArgs @('nonsense-verb') -Expect @(2)
Case -Name 'portals lists instances'      -Group core -TiaArgs @('portals')

if ($foreignPortals.Count) {
    Start-Sleep -Seconds 5
    $closed = @($foreignPortals | Where-Object { -not (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
    [void]$script:Results.Add([pscustomobject]@{
        Name = 'portals leaves open portals open'; Group = 'core'; Command = ''; Expected = ''; Exit = $null
        Verdict = $(if ($closed.Count) { 'FAIL' } else { 'pass' }); Seconds = 5
        Output = $(if ($closed.Count) { 'closed: ' + ($closed -join ', ') } else { 'still running: ' + ($foreignPortals -join ', ') })
        Note = 'Up to 0.3.0, listing a portal closed it.'
    })
    Write-Host ("   [{0}] portals leaves open portals open" -f $(if ($closed.Count) { 'FAIL' } else { 'pass' })) -ForegroundColor $(if ($closed.Count) { 'Red' } else { 'Green' })
} else {
    Skip -Name 'portals leaves open portals open' -Group core -Why 'no TIA Portal was open before the run; open one to test this'
}

Case -Name 'session status with none'     -Group core -TiaArgs @('session status')
Case -Name 'session list with none'       -Group core -TiaArgs @('session', 'list')

# The one-shot path: no session, a headless portal of its own for a single command, gone afterwards.
# --new keeps it away from any portal already open on this machine. It is also the first real
# Openness call of the run, in the foreground - an unapproved tia.exe shows up here as exit 7.
Case -Name 'one-shot with its own portal' -Group core -TiaArgs @('catalog', '1214', '--limit', '3', '--new', '--headless', '--no-daemon')

# With several portals open and no session, tia refuses to guess which one is meant. That is a
# documented behaviour worth checking, and it touches none of those portals. Counted the way tia
# counts them: Windows also lists portals of another TIA version, which this Openness cannot see,
# and the one-shot portal above while it is still closing - neither makes a command ambiguous.
$portalCount = 0
try { $portalCount = @(& $ExePath portals --json 2>$null | ConvertFrom-Json | ForEach-Object { $_ }).Count } catch { }
if ($portalCount -gt 1) {
    Case -Name "ambiguous with $portalCount portals open" -Group core -TiaArgs @('devices') -Expect @(6)
}

# Always a portal of its own. Joining one already open would put the test project into somebody's
# working portal, and with more than one open, a plain 'session start' refuses as ambiguous anyway.
# A portal started with --new belongs to the session, so 'session stop' closes it - and only it.
$sessionArgs = @('session', 'start', '--new')

# Without --openness-version tia drives the newest installed version. The daemon settles on it at
# start, so passing it here covers every command that follows.
if ($OpennessVersion) { $sessionArgs += @('--openness-version', $OpennessVersion) }
$script:Env['opennessUsed'] = if ($OpennessVersion) { $OpennessVersion } else { 'newest installed' }
Note "Openness version under test: $($script:Env['opennessUsed'])"

if ($SkipUi) {
    $sessionArgs += '--headless'
} else {
    Note 'Starting a portal of its own, with a user interface, so the show verbs can be tested.'
}

Case -Name 'session start' -Group core -TiaArgs $sessionArgs
$started = $script:LastPassed

try {
    if (-not $started) {
        # Everything below needs a session. Record the rest as untested rather than letting a wall
        # of consequential failures bury the one that matters.
        foreach ($pending in @(
            @{ n = 'project and hardware'; g = 'core' }, @{ n = 'blocks and tags'; g = 'core' },
            @{ n = 'show verbs';           g = 'ui'   }, @{ n = 'sim start';      g = 'plcsim' },
            @{ n = 'upload and download';  g = 'plc'  }, @{ n = 'archive and retrieve'; g = 'core' },
            @{ n = 'attach and session --project'; g = 'core' })) {
            Skip -Name $pending.n -Group $pending.g -Why 'the session did not start'
        }
        Note 'If exit 7 came back in under two minutes, an unanswered "Openness access" window is'
        Note 'the usual cause. Approve it once, then run this again.'
    } else {

    Case -Name 'session status while running' -Group core -TiaArgs @('session status')

    # ------------------------------------------------------------ project
    Head 'Project and hardware'

    <#  Everything from here on changes whatever project the session has open - and a session that
        joined somebody's portal adopts the project they have open in it. Carrying on in that state
        would add a station, tags and a block to that person's real project, and 'project save'
        would write them to disk. So the run first confirms no project is open, then stops if its
        own project cannot be created.

        The first check is deliberate, not belt-and-braces: 'project new' tests only the project the
        session already holds, and a project opened in the UI is adopted lazily - by the next
        command that asks for one - so it cannot be relied on to notice.  #>
    Case -Name 'no project already open' -Group core -TiaArgs @('project', 'info') -Expect @(4)
    if (-not $script:LastPassed) { throw 'project-new-refused' }

    Case -Name 'project new'  -Group core -TiaArgs @('project', 'new', $ProjectDir, 'FieldTest')
    if (-not $script:LastPassed) { throw 'project-new-refused' }

    Case -Name 'project info' -Group core -TiaArgs @('project', 'info')
    Case -Name 'catalog search' -Group core -TiaArgs @('catalog', '1214')

    $cpu = 'OrderNumber:6ES7 214-1BG40-0XB0/V4.6'
    Case -Name 'device add' -Group core -TiaArgs @('device', 'add', $cpu, 'PLC_1')
    if (-not $script:LastPassed) { $script:NoStation = "'device add' failed, so there is no PLC_1 to work on" }
    Case -Name 'device ip'  -Group core -TiaArgs @('device', 'ip', 'PLC_1', '--address', '192.168.0.10', '--mask', '255.255.255.0', '--subnet', 'PN_1')
    Case -Name 'device ip --router'    -Group core -TiaArgs @('device', 'ip', 'PLC_1', '--router', '192.168.0.1')
    Case -Name 'device ip --no-router' -Group core -TiaArgs @('device', 'ip', 'PLC_1', '--no-router')
    Case -Name 'devices'    -Group core -TiaArgs @('devices')

    Case -Name 'device attrs'          -Group core -TiaArgs @('device', 'attrs', 'PLC_1')
    Case -Name 'device attrs --filter' -Group core -TiaArgs @('device', 'attrs', 'PLC_1', '--filter', 'Name')
    Case -Name 'device set'            -Group core -TiaArgs @('device', 'set', 'PLC_1', 'Comment', 'FieldTest')

    # A current CPU will not compile until this is set, so every compile and download below depends
    # on it. The secret belongs to the throwaway project and is repeated to the downloads.
    Case -Name 'device protection'     -Group core -TiaArgs @('device', 'protection', 'PLC_1', '--level', 'FullAccess', '--secret', $Secret)

    # ------------------------------------------------------------ blocks
    Head 'Blocks and tags'

    $scl = Join-Path $ProjectDir 'Motor.scl'
    New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null
    @'
FUNCTION_BLOCK "Motor"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      start : Bool;
      stop  : Bool;
   END_VAR
   VAR_OUTPUT
      running : Bool;
   END_VAR

BEGIN
   IF #stop THEN
      #running := FALSE;
   ELSIF #start THEN
      #running := TRUE;
   END_IF;
END_FUNCTION_BLOCK
'@ | Set-Content -Path $scl -Encoding utf8

    Case -Name 'table add'  -Group core -TiaArgs @('table', 'add', 'PLC_1', 'FieldTags')
    Case -Name 'tag add'    -Group core -TiaArgs @('tag', 'add', 'PLC_1', 'MotorRun', '--table', 'FieldTags', '--type', 'Bool', '--address', '%Q0.0')
    Case -Name 'tag add without address' -Group core -TiaArgs @('tag', 'add', 'PLC_1', 'Spare', '--table', 'FieldTags', '--type', 'Int')
    Case -Name 'tables'     -Group core -TiaArgs @('tables', 'PLC_1')
    Case -Name 'tags'       -Group core -TiaArgs @('tags', 'PLC_1')
    Case -Name 'tags --table' -Group core -TiaArgs @('tags', 'PLC_1', '--table', 'FieldTags')
    $tagCsv = Join-Path $ProjectDir 'FieldTags.csv'
    Case -Name 'table export' -Group core -TiaArgs @('table', 'export', 'PLC_1', 'FieldTags', '--out', $tagCsv)
    Case -Name 'table import' -Group core -TiaArgs @('table', 'import', 'PLC_1', $tagCsv)
    Case -Name 'tables export' -Group core -TiaArgs @('tables', 'export', 'PLC_1', '--out', (Join-Path $ProjectDir 'all-tags'))
    Case -Name 'tables import' -Group core -TiaArgs @('tables', 'import', 'PLC_1', '--from', (Join-Path $ProjectDir 'all-tags'))
    $tagXml = Join-Path $ProjectDir 'FieldTags.xml'
    Case -Name 'table export --xml' -Group core -TiaArgs @('table', 'export', 'PLC_1', 'FieldTags', '--xml', '--out', $tagXml)
    Case -Name 'table import .xml' -Group core -TiaArgs @('table', 'import', 'PLC_1', $tagXml)
    Case -Name 'tables export --xml' -Group core -TiaArgs @('tables', 'export', 'PLC_1', '--xml', '--out', (Join-Path $ProjectDir 'all-tags-xml'))
    Case -Name 'tables import .xml' -Group core -TiaArgs @('tables', 'import', 'PLC_1', '--from', (Join-Path $ProjectDir 'all-tags-xml'))
    $xml = Join-Path $ProjectDir 'Motor.xml'

    $function = @'
FUNCTION "Twice" : Int
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      value : Int;
   END_VAR

BEGIN
   #Twice := #value * 2;
END_FUNCTION
'@

    Case -Name 'source add'  -Group core -TiaArgs @('source', 'add', 'PLC_1', 'Motor', '--file', $scl)
    Case -Name 'source add from stdin'   -Group core -TiaArgs @('source', 'add', 'PLC_1', 'Twice') -Stdin $function
    Case -Name 'source add --no-generate' -Group core -TiaArgs @('source', 'add', 'PLC_1', 'Parked', '--file', $scl, '--no-generate')
    Case -Name 'source delete ungenerated' -Group core -TiaArgs @('source', 'delete', 'PLC_1', 'Parked', '--force')
    Case -Name 'compile'     -Group core -TiaArgs @('compile', 'PLC_1')
    Case -Name 'compile --json' -Group core -TiaArgs @('compile', 'PLC_1', '--json')
    Case -Name 'blocks'      -Group core -TiaArgs @('blocks', 'PLC_1')
    Case -Name 'blocks --tree' -Group core -TiaArgs @('blocks', 'PLC_1', '--tree')
    Case -Name 'blocks --type' -Group core -TiaArgs @('blocks', 'PLC_1', '--type', 'FB')
    Case -Name 'blocks --system' -Group core -TiaArgs @('blocks', 'PLC_1', '--system')
    Case -Name 'sources'     -Group core -TiaArgs @('sources', 'PLC_1')

    # block show reads the interface back out of an export, so it is the one that proves the
    # SimaticML parsing still matches what this TIA version writes.
    Case -Name 'block show'   -Group core -TiaArgs @('block', 'show', 'PLC_1', 'Motor')
    Case -Name 'block show --json' -Group core -TiaArgs @('block', 'show', 'PLC_1', 'Motor', '--json')
    Case -Name 'block source' -Group core -TiaArgs @('block', 'source', 'PLC_1', 'Motor', '--out', (Join-Path $ProjectDir 'Motor.out.scl'))
    Case -Name 'block source to stdout' -Group core -TiaArgs @('block', 'source', 'PLC_1', 'Twice')
    Case -Name 'block export' -Group core -TiaArgs @('block', 'export', 'PLC_1', 'Motor', '--out', $xml)
    Case -Name 'block export --print' -Group core -TiaArgs @('block', 'export', 'PLC_1', 'Motor', '--print')
    Case -Name 'blocks export' -Group core -TiaArgs @('blocks', 'export', 'PLC_1', '--out', (Join-Path $ProjectDir 'all-xml'))
    Case -Name 'blocks export --source' -Group core -TiaArgs @('blocks', 'export', 'PLC_1', '--source', '--out', (Join-Path $ProjectDir 'all-src'))
    Case -Name 'blocks import' -Group core -TiaArgs @('blocks', 'import', 'PLC_1', '--from', (Join-Path $ProjectDir 'all-src'))

    $udt = @'
TYPE "MotorData"
VERSION : 0.1
   STRUCT
      speed : Int;
      running : Bool;
   END_STRUCT;

END_TYPE
'@
    $udtFile = Join-Path $ProjectDir 'MotorData.udt'
    Case -Name 'source add a data type' -Group core -TiaArgs @('source', 'add', 'PLC_1', 'MotorData.udt') -Stdin $udt
    Case -Name 'types'       -Group core -TiaArgs @('types', 'PLC_1')
    Case -Name 'type export' -Group core -TiaArgs @('type', 'export', 'PLC_1', 'MotorData', '--out', $udtFile)
    Case -Name 'type import' -Group core -TiaArgs @('type', 'import', 'PLC_1', $udtFile)
    Case -Name 'types export' -Group core -TiaArgs @('types', 'export', 'PLC_1', '--out', (Join-Path $ProjectDir 'all-types'))
    Case -Name 'types import' -Group core -TiaArgs @('types', 'import', 'PLC_1', '--from', (Join-Path $ProjectDir 'all-types'))

    Case -Name 'block rename'      -Group core -TiaArgs @('block', 'rename', 'PLC_1', 'Motor', 'MotorRenamed')
    Case -Name 'block rename back' -Group core -TiaArgs @('block', 'rename', 'PLC_1', 'MotorRenamed', 'Motor')

    # The round trip: out of the project and back in, by both routes it offers. The block ends up
    # back at the root under its own name, because the show and transfer cases below expect it there.
    Case -Name 'folder add'      -Group core -TiaArgs @('folder', 'add', 'PLC_1', 'FieldFolder')
    Case -Name 'block delete'    -Group core -TiaArgs @('block', 'delete', 'PLC_1', 'Motor', '--force')
    Case -Name 'source generate' -Group core -TiaArgs @('source', 'generate', 'PLC_1', 'Motor', '--folder', 'FieldFolder')
    Case -Name 'blocks in folder' -Group core -TiaArgs @('blocks', 'PLC_1', '--filter', 'Motor')
    Case -Name 'block delete in folder' -Group core -TiaArgs @('block', 'delete', 'PLC_1', 'FieldFolder/Motor', '--force')
    Case -Name 'folder delete'   -Group core -TiaArgs @('folder', 'delete', 'PLC_1', 'FieldFolder', '--force')
    Case -Name 'block import'    -Group core -TiaArgs @('block', 'import', 'PLC_1', $xml)
    Case -Name 'block import --overwrite' -Group core -TiaArgs @('block', 'import', 'PLC_1', $xml, '--overwrite')
    Case -Name 'compile after import' -Group core -TiaArgs @('compile', 'PLC_1')
    Case -Name 'source delete'   -Group core -TiaArgs @('source', 'delete', 'PLC_1', 'Motor', '--force')

    Case -Name 'devices --json' -Group core -TiaArgs @('devices', '--json')
    Case -Name 'not found exits 5' -Group core -TiaArgs @('blocks', 'NoSuchDevice') -Expect @(5)
    Case -Name 'missing block exits 5' -Group core -TiaArgs @('block', 'show', 'PLC_1', 'NoSuchBlock') -Expect @(5)

    Case -Name 'project save' -Group core -TiaArgs @('project', 'save')

    # CAx hardware export and import. Import only with retain here: it keeps PLC_1 as it is, so the
    # downloads and archives below still work on the station they expect. tools/caxtest.sh is the
    # full round trip into an empty project.
    #
    # CAx exports the PLC tag tables too, and skips a tag that has no address with an error. This
    # project has one on purpose ('tag add without address'), so the export reports exactly that
    # error and exits 1 - with the file written. Exit 0 here would mean CAx changed its behaviour.
    $aml = Join-Path $ProjectDir 'FieldTest.aml'
    $skipNote = 'CAx skips the tag Spare, which has no address; one error is expected.'
    Case -Name 'hw export'          -Group core -TiaArgs @('hw', 'export', $aml) -Expect @(1) -Note $skipNote
    Case -Name 'hw export --device' -Group core -TiaArgs @('hw', 'export', (Join-Path $ProjectDir 'PLC_1.aml'), '--device', 'PLC_1') -Expect @(1) -Note $skipNote
    Case -Name 'hw import --existing retain' -Group core -TiaArgs @('hw', 'import', $aml, '--existing', 'retain')
    Case -Name 'hw import refuses a bad option' -Group core -TiaArgs @('hw', 'import', $aml, '--existing', 'sideways') -Expect @(2)

    # The shell is a prompt, but it reads stdin like anything else, so two lines stand in for a person.
    Case -Name 'shell with piped stdin' -Group core -TiaArgs @('shell') -Stdin "devices`nblocks PLC_1`nexit"

    # ------------------------------------------------------------ ui
    Head 'Editors (needs a portal with a user interface)'

    if ($SkipUi) {
        Skip -Name 'show hw'     -Group ui -Why '-SkipUi was given'
        Skip -Name 'show device' -Group ui -Why '-SkipUi was given'
        Skip -Name 'show block'  -Group ui -Why '-SkipUi was given'
        Skip -Name 'show table'  -Group ui -Why '-SkipUi was given'
    } else {
        Note 'Watch the TIA Portal window: each of these should open an editor.'
        Case -Name 'show hw'     -Group ui -TiaArgs @('show', 'hw')
        Case -Name 'show device' -Group ui -TiaArgs @('show', 'device', 'PLC_1')
        Case -Name 'show block'  -Group ui -TiaArgs @('show', 'block', 'PLC_1', 'Motor')
        Case -Name 'show table'  -Group ui -TiaArgs @('show', 'table', 'PLC_1', 'FieldTags')
    }

    # ------------------------------------------------------------ plcsim
    Head 'Simulation'

    # PLCSIM's API has no S7-1200, and the refusal comes before PLCSIM is touched - so this one runs
    # on any machine, and creates nothing.
    Case -Name 'sim create refuses an S7-1200' -Group core -TiaArgs @('sim', 'create', 'PLC_1') -Expect @(2)

    if ($script:Env['plcsim'] -eq 'present') {
        Note 'Both need a simulation already running at 192.168.0.10: TIA will not start one for a download.'
        Case -Name 'download to PLCSIM' -Group plcsim -TiaArgs @('download', 'PLC_1', '--hardware', '--interface', 'PLCSIM', '--secret', $Secret) `
            -Note 'The route that works today. Fails with exit 5 and a list of interfaces when no simulation is running.'
        Case -Name 'sim start' -Group plcsim -TiaArgs @('sim', 'start', 'PLC_1', '--secret', $Secret) `
            -Note 'In development and documented as not working yet; a failure here is known.'
    } else {
        Skip -Name 'download to PLCSIM' -Group plcsim -Why 'S7-PLCSIM is not installed on this machine'
        Skip -Name 'sim start' -Group plcsim -Why 'S7-PLCSIM is not installed on this machine'
    }

    # ------------------------------------------------------------ plc
    Head 'Real PLC'

    if (-not $PlcAddress) {
        Skip -Name 'upload'   -Group plc -Why 'no -PlcAddress given'
        Skip -Name 'download' -Group plc -Why 'no -PlcAddress given'
    } else {
        $reachable = Test-Connection -ComputerName $PlcAddress -Count 2 -Quiet -ErrorAction SilentlyContinue
        Note "ping $PlcAddress : $(if ($reachable) { 'reachable' } else { 'NO REPLY' })"

        Case -Name 'upload station' -Group plc -TiaArgs @('upload', $PlcAddress)

        if ($plcWriteOk) {
            Case -Name 'download to PLC' -Group plc -TiaArgs @('download', 'PLC_1', '--address', $PlcAddress, '--secret', $Secret)
        } else {
            Skip -Name 'download to PLC' -Group plc -Why 'needs -AllowPlcWrite and confirmation; it stops and reloads the controller'
        }
    }

    # ------------------------------------------------------------ archive
    Head 'Archive and retrieve'

    # Last of the project work on purpose: retrieve leaves the session holding the unpacked copy,
    # and nothing after this needs more than some project being open.
    Case -Name 'table add before archive'      -Group core -TiaArgs @('table', 'add', 'PLC_1', 'ArchiveProbe')
    if ($script:LastPassed) {
        Case -Name 'archive refuses unsaved changes' -Group core -TiaArgs @('project', 'archive') -Expect @(2)
    } else {
        Skip -Name 'archive refuses unsaved changes' -Group core -Why 'nothing could be changed to make the project unsaved'
    }
    Case -Name 'project archive --save'        -Group core -TiaArgs @('project', 'archive', '--save')
    $archived = $script:LastPassed

    # The extension follows the TIA version (.zap20, .zap19), so both files are found rather than named.
    $zap      = Get-ChildItem -LiteralPath $ProjectDir -Filter 'FieldTest.zap*' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $original = Get-ChildItem -LiteralPath (Join-Path $ProjectDir 'FieldTest') -Filter 'FieldTest.ap*' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $originalClosed = $false

    if (-not $archived -or -not $zap) {
        Skip -Name 'archive overwrite and retrieve' -Group core -Why 'no archive was written beside the project folder'
    } else {
        Note "archive: $($zap.FullName) ($($zap.Length) bytes)"
        Case -Name 'archive refuses to overwrite'  -Group core -TiaArgs @('project', 'archive') -Expect @(2)
        Case -Name 'archive --overwrite --minimize' -Group core -TiaArgs @('project', 'archive', '--overwrite', '--minimize')

        Case -Name 'project close' -Group core -TiaArgs @('project', 'close')
        $originalClosed = $script:LastPassed
        Case -Name 'open refuses an archive' -Group core -TiaArgs @('project', 'open', $zap.FullName) -Expect @(2)

        # The plain way back in, before the archive's: open from disk, and close with a save.
        if ($original) {
            Case -Name 'project open'         -Group core -TiaArgs @('project', 'open', $original.FullName)
            Case -Name 'project close --save' -Group core -TiaArgs @('project', 'close', '--save')
            $originalClosed = $script:LastPassed
        }

        Case -Name 'project retrieve' -Group core -TiaArgs @('project', 'retrieve', $zap.FullName, $RetrieveDir)

        if ($script:LastPassed) {
            Case -Name 'devices after retrieve' -Group core -TiaArgs @('devices')
            Case -Name 'tables after retrieve'  -Group core -TiaArgs @('tables', 'PLC_1')
        } elseif ($original) {
            # The cases below want a project open; put the original back for them.
            Case -Name 'reopen after failed retrieve' -Group core -TiaArgs @('project', 'open', $original.FullName)
            $originalClosed = $false
        }
    }

    # ------------------------------------------------------------ regressions
    Head 'Regressions'

    # 0.1.0: 'tia portals' during a session disposed the session's own portal, and every later command
    # failed with exit 9. Late in the run, so a regression cannot take the other tests down with it.
    Case -Name 'portals during a session' -Group core -TiaArgs @('portals')
    Case -Name 'session survives portals' -Group core -TiaArgs @('project', 'info')

    # ------------------------------------------------------------ attach
    Head 'Attaching'

    # What every command does when no session is running and a portal is open: a second client joins
    # the portal for one command and lets go again. Here the portal is the session's own, so the case
    # after it checks that letting go did not take that portal with it.
    $portalPid = $null
    try { $portalPid = (& $ExePath session status --json 2>$null | ConvertFrom-Json).session.processId } catch { }

    if ($portalPid) {
        $attachArgs = @('project', 'info', '--no-daemon', '--attach', "$portalPid")
        if ($OpennessVersion) { $attachArgs += @('--openness-version', $OpennessVersion) }
        Case -Name 'attach by pid'                    -Group core -TiaArgs $attachArgs
        Case -Name 'session survives a second client' -Group core -TiaArgs @('devices')
    } else {
        Skip -Name 'attach by pid' -Group core -Why "session status did not give the portal's process id"
    }

    # ------------------------------------------------------------ session --project
    Head 'Session that opens a project'

    # A second session, headless this time, that opens the saved project as it starts. Only when the
    # first session let go of that project: its portal takes a few seconds to close, and a project
    # it still holds is locked until then.
    if ($original -and $originalClosed) {
        Case -Name 'session stop before reopening' -Group core -TiaArgs @('session', 'stop')
        Case -Name 'session start --project' -Group core -TiaArgs @('session', 'start', '--new', '--headless', '--project', $original.FullName)

        if ($script:LastPassed) {
            Case -Name 'project info after --project'  -Group core -TiaArgs @('project', 'info')
            Case -Name 'blocks survive save and reopen' -Group core -TiaArgs @('blocks', 'PLC_1')
            Case -Name 'tags survive save and reopen'   -Group core -TiaArgs @('tags', 'PLC_1', '--table', 'FieldTags')
            Case -Name 'show refuses a headless portal' -Group core -TiaArgs @('show', 'hw') -Expect @(2)
        }
    } else {
        Skip -Name 'session start --project' -Group core -Why 'the first session still holds the test project'
    }

    # ------------------------------------------------------------ several sessions
    Head 'Several sessions'

    # One session per portal: a second, headless one beside the first. With two running, a command
    # naming no portal is refused rather than sent to either, --attach and TIA_PORTAL each pick
    # one, and stopping one leaves the other running.
    $firstPid = $null
    try { $firstPid = (& $ExePath session status --json 2>$null | ConvertFrom-Json).session.processId } catch { }

    if ($firstPid) {
        Case -Name 'second session beside the first' -Group core -TiaArgs @('session', 'start', '--new', '--headless')

        $secondPid = $null
        try {
            $secondPid = @(& $ExePath session list --json 2>$null | ConvertFrom-Json | ForEach-Object { $_ } |
                ForEach-Object { $_.session.processId } | Where-Object { $_ -and $_ -ne $firstPid })[0]
        } catch { }

        if ($secondPid) {
            Case -Name 'session list'                      -Group core -TiaArgs @('session', 'list')
            Case -Name 'no portal named with two sessions' -Group core -TiaArgs @('project', 'info') -Expect @(6)
            Case -Name 'attach picks a session'            -Group core -TiaArgs @('devices', '--attach', "$firstPid")

            # The second session has no project, so exit 4 is the proof it was the one that answered.
            $env:TIA_PORTAL = "$secondPid"
            try { Case -Name 'TIA_PORTAL picks a session' -Group core -TiaArgs @('project', 'info') -Expect @(4) }
            finally { Remove-Item Env:\TIA_PORTAL -ErrorAction SilentlyContinue }

            Case -Name 'session stop --attach'   -Group core -TiaArgs @('session', 'stop', '--attach', "$secondPid")
            Case -Name 'first session survives'  -Group core -TiaArgs @('devices')
        } else {
            Skip -Name 'session list' -Group core -Why 'the second session did not start, or did not report its portal'
        }
    } else {
        Skip -Name 'second session beside the first' -Group core -Why "session status did not give the portal's process id"
    }

    # ------------------------------------------------------------ workspace
    Head 'Workspace'

    # A workspace made from the archive above. Inside it, commands must reach its project and no
    # other: the session still holding FieldTest is somebody else's project as far as it knows.
    # TIA_WORKSPACE stands in for running from inside the folder.
    if ($zap) {
        $wsDir = Join-Path $ProjectDir 'ws'
        Case -Name 'init --from an archive' -Group core -TiaArgs @('init', $wsDir, '--from', $zap.FullName)

        $env:TIA_WORKSPACE = $wsDir
        try {
            Case -Name 'workspace'                          -Group core -TiaArgs @('workspace')
            Case -Name 'nothing open there: commands refuse' -Group core -TiaArgs @('devices') -Expect @(3)
            Case -Name 'open retrieves the archive'         -Group core -TiaArgs @('open', '--headless')
            if ($script:LastPassed) {
                Case -Name 'commands go to the workspace project' -Group core -TiaArgs @('project', 'info')
                Case -Name 'workspace shows it open'              -Group core -TiaArgs @('workspace')
                Case -Name 'open again finds the session'         -Group core -TiaArgs @('open')
                # Two sessions now, and without the workspace nothing says which.
                Case -Name '--no-workspace ignores it' -Group core -TiaArgs @('project', 'info', '--no-workspace') -Expect @(6)
                Case -Name 'session stop in the workspace' -Group core -TiaArgs @('session', 'stop')
                Case -Name 'the other session survives' -Group core -TiaArgs @('session', 'list')
            }
        } finally {
            Remove-Item Env:\TIA_WORKSPACE -ErrorAction SilentlyContinue
        }
    } else {
        Skip -Name 'init --from an archive' -Group core -Why 'no archive was made earlier in the run'
    }
    } # end of the "session started" branch

} catch {
    # The only failure handled here is the deliberate stop above; anything else is a real fault in
    # the harness and should surface as one.
    if ($_.Exception.Message -ne 'project-new-refused') { throw }

    foreach ($pending in @(
        @{ n = 'hardware, blocks and tags'; g = 'core'   }, @{ n = 'show verbs';          g = 'ui'  },
        @{ n = 'sim start';                 g = 'plcsim' }, @{ n = 'upload and download'; g = 'plc' },
        @{ n = 'archive and retrieve';      g = 'core'   }, @{ n = 'attach and session --project'; g = 'core' })) {
        Skip -Name $pending.n -Group $pending.g -Why 'the test project could not be created'
    }
    Note 'Stopped: the test project could not be created, and carrying on would have changed'
    Note 'whatever project the portal has open instead. If you have one open, save and close it -'
    Note 'or close TIA Portal entirely so the harness starts its own - then run again.'
} finally {
    # A session left running owns a TIA Portal that nothing else will close.
    Head 'Cleanup'
    Case -Name 'session stop' -Group core -TiaArgs @('session', 'stop', '--all') | Out-Null

    if (-not $KeepProject) {
        # The portal keeps the project's files locked for a few seconds after 'session stop' returns.
        foreach ($attempt in 1..10) {
            try { Remove-Item -LiteralPath $ProjectDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
            if (-not (Test-Path -LiteralPath $ProjectDir)) { break }
            Start-Sleep -Seconds 3
        }
        if (Test-Path -LiteralPath $ProjectDir) { Note "Could not delete $ProjectDir - still locked. It is safe to delete by hand." }
    } else {
        Note "Project kept at $ProjectDir"
    }

    # ------------------------------------------------------------ report
    if (-not (Test-Path $ReportDir)) { New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $md    = Join-Path $ReportDir "fieldtest-$stamp.md"
    $json  = Join-Path $ReportDir "fieldtest-$stamp.json"

    $pass = @($script:Results | Where-Object Verdict -eq 'pass').Count
    $fail = @($script:Results | Where-Object Verdict -eq 'FAIL').Count
    $skip = @($script:Results | Where-Object Verdict -eq 'skipped').Count

    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine("# tia-cli field test - $stamp")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("**$pass passed, $fail failed, $skip skipped**")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Machine')
    [void]$sb.AppendLine()
    foreach ($k in $script:Env.Keys) { [void]$sb.AppendLine("- **$k**: $($script:Env[$k])") }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Results')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Test | Group | Verdict | Exit | Expected | Seconds |')
    [void]$sb.AppendLine('| --- | --- | --- | --- | --- | --- |')
    foreach ($r in $script:Results) {
        [void]$sb.AppendLine("| $($r.Name) | $($r.Group) | $($r.Verdict) | $($r.Exit) | $($r.Expected) | $($r.Seconds) |")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Transcript')
    foreach ($r in $script:Results) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("### $($r.Name) [$($r.Verdict)]")
        [void]$sb.AppendLine()
        if ($r.Command) { [void]$sb.AppendLine('`' + $r.Command + '`') ; [void]$sb.AppendLine() }
        if ($r.Note)    { [void]$sb.AppendLine("_$($r.Note)_") ; [void]$sb.AppendLine() }
        if ($r.Output)  {
            [void]$sb.AppendLine('```')
            [void]$sb.AppendLine($r.Output)
            [void]$sb.AppendLine('```')
        }
    }

    Set-Content -Path $md -Value $sb.ToString() -Encoding utf8
    [pscustomobject]@{ environment = $script:Env; results = $script:Results } |
        ConvertTo-Json -Depth 6 | Set-Content -Path $json -Encoding utf8

    Write-Host ''
    Write-Host "$pass passed, $fail failed, $skip skipped" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
    Write-Host ''
    Write-Host 'Send these two files back:'
    Write-Host "  $md"
    Write-Host "  $json"
    Write-Host ''
}
