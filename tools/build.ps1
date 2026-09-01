<#
.SYNOPSIS
    Builds tia.exe into a folder you can put on PATH.

.DESCRIPTION
    Publishes the CLI to dist\ by default. Publish to a stable location and leave it there: TIA
    Portal's Openness whitelist records the exe's path *and* a hash of its bytes, so both moving and
    rebuilding tia.exe cost you one more "Openness access" confirmation.

.PARAMETER OpennessAssemblyPath
    Siemens.Engineering.dll to compile against. Only the reference matters - at runtime the assembly
    is located through the registry, so a build made against V20 also drives V19 or V21.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path $PSScriptRoot '..\dist'),
    [string]$OpennessAssemblyPath,
    [switch]$AddToPath
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = [IO.Path]::GetFullPath($Output)

$buildArgs = @()
if ($OpennessAssemblyPath) { $buildArgs += "-p:OpennessAssemblyPath=$OpennessAssemblyPath" }

Write-Host "Publishing tia -> $out"
dotnet publish (Join-Path $repo 'src\Tia.Cli\Tia.Cli.csproj') -c $Configuration -o $out @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $out 'tia.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist." }

if ($AddToPath) {
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($user -split ';' -notcontains $out) {
        [Environment]::SetEnvironmentVariable('Path', "$user;$out", 'User')
        Write-Host "Added $out to your user PATH. Open a new terminal to pick it up."
    } else {
        Write-Host "$out is already on your user PATH."
    }
}

Write-Host ''
Write-Host "Done: $exe"
Write-Host 'First run: TIA Portal shows an "Openness access" window and waits until you answer it.'
Write-Host 'That approval covers this exact build - rebuilding tia.exe asks once more.'
