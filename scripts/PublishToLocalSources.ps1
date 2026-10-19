param (
    [string]$localSourcesDir = "",
    [ValidateSet("Debug", "Release")]
    [string]$buildType = "Release",
    [switch]$skipBuild,
    [switch]$keepPackages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-DefaultLocalSourcesDir {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return "D:/nuget.local"
    }

    return Join-Path -Path $HOME -ChildPath ".nuget/local"
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Push-NuGetPackages {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $true)]
        [string]$Source,

        [switch]$KeepPackages
    )

    $packages = Get-ChildItem -Path $PackagePath -Filter *.nupkg -Recurse -File
    if (-not $packages) {
        throw "No .nupkg files were found in $PackagePath."
    }

    foreach ($pkg in $packages) {
        $pushArguments = @(
            "nuget",
            "push",
            $pkg.FullName,
            "--source",
            $Source
        )

        if (-not (Test-Path -Path $Source -PathType Container)) {
            $pushArguments += "--skip-duplicate"
        }

        Invoke-DotNet -Arguments $pushArguments

        if (-not $KeepPackages) {
            Remove-Item -Path $pkg.FullName -Force
        }

        Write-Host "Published: $($pkg.Name)" -ForegroundColor Green
    }
}

if ([string]::IsNullOrWhiteSpace($localSourcesDir)) {
    $localSourcesDir = Get-DefaultLocalSourcesDir
}

$repoRoot = (Resolve-Path -Path (Join-Path -Path $PSScriptRoot -ChildPath "..")).Path
$packageOutputPath = Join-Path -Path $repoRoot -ChildPath ".artifacts/Nuget/LocalSource/$buildType"
$packageProjects = @(
    "src/AtomUI.Foundation.Generator/AtomUI.Foundation.Generator.csproj",
    "src/AtomUI.Foundation/AtomUI.Foundation.csproj"
)

New-Item -Path $localSourcesDir -ItemType Directory -Force | Out-Null

if (Test-Path -Path $packageOutputPath -PathType Container) {
    Remove-Item -Path $packageOutputPath -Recurse -Force
}
New-Item -Path $packageOutputPath -ItemType Directory -Force | Out-Null

foreach ($project in $packageProjects) {
    $projectPath = Join-Path -Path $repoRoot -ChildPath $project

    if (-not $skipBuild) {
        Invoke-DotNet -Arguments @(
            "build",
            "--configuration",
            $buildType,
            $projectPath
        )
    }

    Invoke-DotNet -Arguments @(
        "pack",
        "--no-build",
        "--configuration",
        $buildType,
        "-p:PackageOutputPath=$packageOutputPath",
        $projectPath
    )
}

Push-NuGetPackages `
    -PackagePath $packageOutputPath `
    -Source $localSourcesDir `
    -KeepPackages:$keepPackages
