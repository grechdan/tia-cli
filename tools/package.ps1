<#
.SYNOPSIS
    Builds the release zip.

.DESCRIPTION
    Runs the unit tests, publishes a fresh build, and writes dist\tia-cli-<version>-win-x64.zip
    with install.ps1 and uninstall.ps1 alongside the binaries, plus a .sha256 next to it.

    Always packages from a fresh publish rather than whatever is lying in dist\ - shipping a stale
    dist\ is exactly how a release goes out missing the commands it claims to have.

.PARAMETER SkipTests
    Package without running the unit tests first. For a real release, do not.

.PARAMETER OpennessAssemblyPath
    Siemens.Engineering.dll to compile against, if TIA Portal is not at the default location.

.EXAMPLE
    powershell -File tools/package.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OpennessAssemblyPath,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $repo 'dist'

# ---------------------------------------------------------------- tests
if ($SkipTests) {
    Write-Host 'Skipping tests (-SkipTests).' -ForegroundColor Yellow
} else {
    Write-Host 'Running unit tests'
    dotnet test (Join-Path $repo 'src\Tia.Cli.Tests\Tia.Cli.Tests.csproj') -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not packaging.' }
}

# ---------------------------------------------------------------- build
$stage = Join-Path ([IO.Path]::GetTempPath()) ('tia-cli-pkg-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    Write-Host ''
    Write-Host 'Publishing'
    $buildArgs = @('-c', $Configuration, '-o', $stage, '--nologo', '-v', 'quiet')
    if ($OpennessAssemblyPath) { $buildArgs += "-p:OpennessAssemblyPath=$OpennessAssemblyPath" }
    dotnet publish (Join-Path $repo 'src\Tia.Cli\Tia.Cli.csproj') @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $exe = Join-Path $stage 'tia.exe'
    if (-not (Test-Path $exe)) { throw "Expected $exe to exist." }

    $version = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $version) { throw 'Could not read a version off tia.exe.' }
    $version = $version.Split('+')[0]

    # Symbols are useful while debugging but are not part of what ships.
    Get-ChildItem -Path $stage -Filter '*.pdb' | Remove-Item -Force

    # ---------------------------------------------------------------- assemble
    $name = "tia-cli-$version"
    $folder = Join-Path $stage $name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null

    Get-ChildItem -Path $stage -File | ForEach-Object {
        Move-Item -Path $_.FullName -Destination (Join-Path $folder $_.Name) -Force
    }

    Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $folder -Force
    Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $folder -Force
    Copy-Item (Join-Path $repo 'README.md') $folder -Force
    foreach ($extra in @('LICENSE', 'LICENSE.txt', 'LICENSE.md', 'CHANGELOG.md')) {
        $path = Join-Path $repo $extra
        if (Test-Path $path) { Copy-Item $path $folder -Force }
    }

    @"
tia-cli $version

To install, from this folder:

    powershell -ExecutionPolicy Bypass -File install.ps1

That copies the tool to %LOCALAPPDATA%\Programs\tia-cli and puts it on your PATH. No administrator
rights are needed. Open a new terminal afterwards, then run 'tia help'.

To remove it later:

    powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\tia-cli\uninstall.ps1"

Requirements and everything else are in README.md.
"@ | Set-Content -Path (Join-Path $folder 'INSTALL.txt') -Encoding utf8

    # ---------------------------------------------------------------- zip
    if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }
    $zip = Join-Path $dist "$name-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content -Path "$zip.sha256" -Encoding utf8

    $size = [Math]::Round((Get-Item $zip).Length / 1KB)
    Write-Host ''
    Write-Host "Packaged tia $version" -ForegroundColor Green
    Write-Host "  $zip  (${size} KB)"
    Write-Host "  $zip.sha256"
    Write-Host ''
    Write-Host '  Contents:'
    Get-ChildItem $folder -File | ForEach-Object { Write-Host "    $($_.Name)" }
    Write-Host ''
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
