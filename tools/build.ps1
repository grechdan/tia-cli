<#
.SYNOPSIS
    Builds tia.exe for working on it. Not how it gets installed.

.DESCRIPTION
    Publishes the CLI to dist\ so you have something to run while changing it: build, try it against
    a portal, repeat. It is the inner loop and nothing more.

    It deliberately does not touch your PATH. Installing is install.ps1's job, from the zip that
    tools/package.ps1 builds, and PATH belongs to it alone - two scripts adding two different folders
    is how you end up with a 'tia' that answers from somewhere you did not expect and survives an
    uninstall.

.PARAMETER OpennessAssemblyPath
    Siemens.Engineering.dll to compile against. Only the reference matters - at runtime the assembly
    is located through the registry, so a build made against V20 also drives V19 or V21.
#>
# No [CmdletBinding()] here: it would empty $PSScriptRoot in the -Output default below, which is
# evaluated before the body runs.
param(
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path $PSScriptRoot '..\dist'),
    [string]$OpennessAssemblyPath
)

$ErrorActionPreference = 'Stop'

# Undeclared arguments land in $args rather than failing, so -AddToPath would quietly do nothing.
if ($args -contains '-AddToPath') {
    throw '-AddToPath is gone: PATH belongs to install.ps1 now. Build the zip with tools/package.ps1 and install from that.'
}

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = [IO.Path]::GetFullPath($Output)

$buildArgs = @()
if ($OpennessAssemblyPath) { $buildArgs += "-p:OpennessAssemblyPath=$OpennessAssemblyPath" }

Write-Host "Publishing tia -> $out"
dotnet publish (Join-Path $repo 'src\Tia.Cli\Tia.Cli.csproj') -c $Configuration -o $out @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $out 'tia.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist." }

Write-Host ''
Write-Host "Done: $exe"
Write-Host 'First run: TIA Portal shows an "Openness access" window and waits until you answer it.'
Write-Host 'That approval covers this exact build - rebuilding tia.exe asks once more.'
