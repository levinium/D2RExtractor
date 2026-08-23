param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,

    [Parameter(Mandatory = $true)]
    [string]$Configuration,

    [Parameter(Mandatory = $true)]
    [string]$CascLibProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$CascLibOutputPath,

    [Parameter(Mandatory = $true)]
    [string]$CascLibToolsPath
)

$ErrorActionPreference = 'Stop'

function Get-GitPath {
    $gitCandidates = @(
        (Get-Command git -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        'C:\Program Files\Git\cmd\git.exe',
        'C:\Program Files\Git\bin\git.exe'
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    return $gitCandidates | Select-Object -First 1
}

function Get-MSBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'vswhere.exe not found. Install Visual Studio Build Tools.'
    }

    $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1

    if (-not $installPath -or -not $msbuild) {
        throw 'MSBuild.exe not found. Install Visual Studio Build Tools.'
    }

    return @{
        InstallPath = $installPath
        MSBuildPath = $msbuild
    }
}

function Get-PlatformToolset([string]$InstallPath) {
    $platformProps = Get-ChildItem (Join-Path $InstallPath 'MSBuild\Microsoft\VC') -Recurse -Filter 'Platform.Default.props' |
        Where-Object { $_.FullName -like '*\Platforms\x64\Platform.Default.props' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $platformProps) {
        throw 'Platform.Default.props not found.'
    }

    $platformToolset = ([regex]::Match(
        (Get-Content $platformProps.FullName -Raw),
        '<DefaultPlatformToolset[^>]*>([^<]+)</DefaultPlatformToolset>'
    )).Groups[1].Value

    if (-not $platformToolset) {
        throw 'Could not determine the default MSVC PlatformToolset.'
    }

    return $platformToolset
}

function Resolve-FullPath([string]$BasePath, [string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $PathValue))
}

$RepoRoot = (Resolve-Path $RepoRoot).Path
$CascLibProjectPath = Resolve-FullPath -BasePath $RepoRoot -PathValue $CascLibProjectPath
$CascLibOutputPath = Resolve-FullPath -BasePath $RepoRoot -PathValue $CascLibOutputPath
$CascLibToolsPath = Resolve-FullPath -BasePath $RepoRoot -PathValue $CascLibToolsPath

if (-not (Test-Path $CascLibProjectPath)) {
    if (-not (Test-Path (Join-Path $RepoRoot '.gitmodules'))) {
        throw 'CascLib submodule is missing and .gitmodules was not found.'
    }

    $git = Get-GitPath
    if (-not $git) {
        throw 'git.exe not found. Install Git or initialize the CascLib submodule manually.'
    }

    & $git -C $RepoRoot submodule update --init --recursive external/CascLib

    if (-not (Test-Path $CascLibProjectPath)) {
        throw 'CascLib submodule initialization failed.'
    }
}

$buildTools = Get-MSBuildPath
$platformToolset = Get-PlatformToolset -InstallPath $buildTools.InstallPath
$cascLibSourceDir = Split-Path -Parent $CascLibProjectPath
$afxresShim = Join-Path (Join-Path $cascLibSourceDir 'src') 'afxres.h'
$createdShim = $false

if (-not (Test-Path $afxresShim)) {
    Set-Content -LiteralPath $afxresShim -Value '#include <winres.h>'
    $createdShim = $true
}

try {
    & $buildTools.MSBuildPath $CascLibProjectPath "/p:Configuration=$Configuration" /p:Platform=x64 "/p:PlatformToolset=$platformToolset" /m /nologo

    if (-not (Test-Path $CascLibOutputPath)) {
        throw 'CascLib.dll was not produced at the expected location.'
    }

    $toolsDir = Split-Path -Parent $CascLibToolsPath
    if (-not (Test-Path $toolsDir)) {
        New-Item -ItemType Directory -Path $toolsDir | Out-Null
    }

    Copy-Item -LiteralPath $CascLibOutputPath -Destination $CascLibToolsPath -Force
}
finally {
    if ($createdShim -and (Test-Path $afxresShim)) {
        Remove-Item -LiteralPath $afxresShim -Force
    }
}
