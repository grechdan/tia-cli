<#
.SYNOPSIS
    Runs the tia-cli test matrix on a machine that has the hardware, and writes a report to send back.

.DESCRIPTION
    Everything here runs against an installed tia.exe. Nothing is compiled, so the machine needs no
    development tools - PowerShell and the installed CLI are enough.

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
$projectFolder = Join-Path $ProjectDir 'FieldTest'
if ($projectFolder.Length -gt 143) {
    throw "The test project would be created at $projectFolder ($($projectFolder.Length) characters), and TIA Portal allows at most 143. Pass a shorter -ProjectDir, such as C:\tia-test."
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
        [string]   $Note
    )

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
        $output = (& $ExePath @TiaArgs 2>&1 | ForEach-Object {
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
        if ($TiaArgs[1] -eq 'stop')  { $script:InSession = $false }
    }

    [void]$script:Results.Add([pscustomobject]@{
        Name = $Name; Group = $Group; Command = 'tia ' + ($TiaArgs -join ' ')
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
try { $sessionState = (& $ExePath session status 2>&1 | Out-String) }
finally { $ErrorActionPreference = $previous }

if ($sessionState -match 'Session running') {
    throw "A tia session is already running. The field test needs one of its own, and sharing yours would end it. Save anything open in it, run 'tia session stop', then run this again."
}

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

Case -Name 'portals lists instances'      -Group core -TiaArgs @('portals')
Case -Name 'session status with none'     -Group core -TiaArgs @('session status')

# With several portals open and no session, tia refuses to guess which one is meant. That is a
# documented behaviour worth checking, and it touches none of those portals.
$portalCount = @(Get-Process -Name 'Siemens.Automation.Portal' -ErrorAction SilentlyContinue).Count
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
            @{ n = 'upload and download';  g = 'plc'  })) {
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
    Case -Name 'device ip'  -Group core -TiaArgs @('device', 'ip', 'PLC_1', '--address', '192.168.0.10', '--mask', '255.255.255.0', '--subnet', 'PN_1')
    Case -Name 'devices'    -Group core -TiaArgs @('devices')

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
    $xml = Join-Path $ProjectDir 'Motor.xml'

    Case -Name 'source add'  -Group core -TiaArgs @('source', 'add', 'PLC_1', 'Motor', '--file', $scl)
    Case -Name 'compile'     -Group core -TiaArgs @('compile', 'PLC_1')
    Case -Name 'blocks'      -Group core -TiaArgs @('blocks', 'PLC_1')
    Case -Name 'blocks --tree' -Group core -TiaArgs @('blocks', 'PLC_1', '--tree')
    Case -Name 'blocks --type' -Group core -TiaArgs @('blocks', 'PLC_1', '--type', 'FB')
    Case -Name 'sources'     -Group core -TiaArgs @('sources', 'PLC_1')

    # block show reads the interface back out of an export, so it is the one that proves the
    # SimaticML parsing still matches what this TIA version writes.
    Case -Name 'block show'   -Group core -TiaArgs @('block', 'show', 'PLC_1', 'Motor')
    Case -Name 'block source' -Group core -TiaArgs @('block', 'source', 'PLC_1', 'Motor', '--out', (Join-Path $ProjectDir 'Motor.out.scl'))
    Case -Name 'block export' -Group core -TiaArgs @('block', 'export', 'PLC_1', 'Motor', '--out', $xml)

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
    Case -Name 'compile after import' -Group core -TiaArgs @('compile', 'PLC_1')
    Case -Name 'source delete'   -Group core -TiaArgs @('source', 'delete', 'PLC_1', 'Motor', '--force')

    Case -Name 'devices --json' -Group core -TiaArgs @('devices', '--json')
    Case -Name 'not found exits 5' -Group core -TiaArgs @('blocks', 'NoSuchDevice') -Expect @(5)
    Case -Name 'missing block exits 5' -Group core -TiaArgs @('block', 'show', 'PLC_1', 'NoSuchBlock') -Expect @(5)

    Case -Name 'project save' -Group core -TiaArgs @('project', 'save')

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

    if ($script:Env['plcsim'] -eq 'present') {
        Case -Name 'sim start' -Group plcsim -TiaArgs @('sim', 'start', 'PLC_1')
    } else {
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
            Case -Name 'download to PLC' -Group plc -TiaArgs @('download', 'PLC_1', '--address', $PlcAddress)
        } else {
            Skip -Name 'download to PLC' -Group plc -Why 'needs -AllowPlcWrite and confirmation; it stops and reloads the controller'
        }
    }

    # ------------------------------------------------------------ regressions
    Head 'Regressions'

    # 0.1.0: 'tia portals' during a session disposed the session's own portal, and every later command
    # failed with exit 9. Last in the run, so a regression cannot take the other tests down with it.
    Case -Name 'portals during a session' -Group core -TiaArgs @('portals')
    Case -Name 'session survives portals' -Group core -TiaArgs @('project', 'info')
    } # end of the "session started" branch

} catch {
    # The only failure handled here is the deliberate stop above; anything else is a real fault in
    # the harness and should surface as one.
    if ($_.Exception.Message -ne 'project-new-refused') { throw }

    foreach ($pending in @(
        @{ n = 'hardware, blocks and tags'; g = 'core'   }, @{ n = 'show verbs';          g = 'ui'  },
        @{ n = 'sim start';                 g = 'plcsim' }, @{ n = 'upload and download'; g = 'plc' })) {
        Skip -Name $pending.n -Group $pending.g -Why 'the test project could not be created'
    }
    Note 'Stopped: the test project could not be created, and carrying on would have changed'
    Note 'whatever project the portal has open instead. If you have one open, save and close it -'
    Note 'or close TIA Portal entirely so the harness starts its own - then run again.'
} finally {
    # A session left running owns a TIA Portal that nothing else will close.
    Head 'Cleanup'
    Case -Name 'session stop' -Group core -TiaArgs @('session', 'stop') | Out-Null

    if (-not $KeepProject) {
        try { Remove-Item -LiteralPath $ProjectDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
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
