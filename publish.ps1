<#
.SYNOPSIS
    Builds D2RExtractor as one self-contained executable and zips it for release.

.DESCRIPTION
    Produces a single .exe with the .NET runtime inside it, so installing the app
    is unzipping one folder. Nothing else has to be present on the machine.

    The zip is named to match what the releases page already serves:
    D2RExtractor-Compiled-Standalone_v<version>.zip, with the version read from
    the csproj rather than typed in here, so the two cannot drift.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SponsorUrl "https://github.com/sponsors/levinium?frequency=one-time&amount={amount}"
#>
[CmdletBinding()]
param(
    # Where the zip lands. Defaults to dist/ off the repo root.
    [string] $OutputDirectory,

    # Where "Support D2R File Extractor" sends people. Left unset - as it is for
    # every build from a clean checkout - the app has no donate button at all,
    # so a fork cannot ship one asking on someone else's behalf.
    #
    # An {amount} placeholder turns the ask into a picker, and is only worth
    # including where the destination actually reads the amount out of the URL.
    # GitHub Sponsors does, via ?frequency=one-time&amount=N.
    [string] $SponsorUrl
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$csproj  = Join-Path $root 'D2RExtractor\D2RExtractor.csproj'
$staging = Join-Path $root 'D2RExtractor\bin\x64\Release\net8.0-windows\publish\win-x64'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj." }

# CascLib.dll is not in the repository - it is a native binary the builder
# supplies - and its absence is a Condition in the csproj rather than an error,
# so a publish without it succeeds and ships an app that cannot open a
# Battle.net install. Fail here instead, where it is still cheap to notice.
$casclib = Join-Path $root 'D2RExtractor\Tools\CascLib.dll'
if (-not (Test-Path $casclib)) {
    throw "CascLib.dll is missing from D2RExtractor\Tools\. Battle.net installs would not open."
}

Write-Host "Publishing D2RExtractor v$version..."

# [string[]] and the leading comma are both load-bearing. An `if` used as an
# expression yields its branch's OUTPUT, and PowerShell enumerates a one-element
# array down to a bare string on the way out - after which `@sponsor` splats a
# string, which it does one character at a time.
[string[]] $sponsor = if ($SponsorUrl) { , "-p:SponsorUrl=$SponsorUrl" } else { @() }

if ($SponsorUrl) {
    Write-Host "  Support button enabled: $SponsorUrl"
} else {
    Write-Host '  No support button (pass -SponsorUrl to include one).'
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

dotnet publish $csproj `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $staging `
    --nologo `
    @sponsor

if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
}

$zip = Join-Path $OutputDirectory "D2RExtractor-Compiled-Standalone_v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $zip ($size MB)"
