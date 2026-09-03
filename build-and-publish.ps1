<#
.SYNOPSIS
    Automated build, publish, and packaging script for RhythmHub (WinUI 3 unpackaged).

.DESCRIPTION
    Compiles RhythmHub in Release mode as a self-contained win-x64 app to a clean staging directory (dist/staged).
    If Inno Setup compiler (ISCC.exe) is available, compiles installer.iss into dist/installer/RhythmHubSetup.exe.

.EXAMPLE
    .\build-and-publish.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller = $false
)

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
$projectFile = Join-Path $scriptDir "RhythmHub.csproj"
$stagingDir = Join-Path $scriptDir "dist\staged"
$installerOutputDir = Join-Path $scriptDir "dist\installer"
$installerIss = Join-Path $scriptDir "installer.iss"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "       RhythmHub Build & Distribution Packaging Pipeline    " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Project File:  $projectFile"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $Runtime"
Write-Host "Staging Path:  $stagingDir"
Write-Host "Installer Out: $installerOutputDir"
Write-Host ""

# 1. Clean Staging and Output Directories
if (Test-Path $stagingDir) {
    Write-Host "[1/3] Cleaning staging directory..." -ForegroundColor Yellow
    try {
        Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction Stop
    } catch {
        Start-Sleep -Seconds 1
        Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if (Test-Path $installerOutputDir) {
    Write-Host "[1/3] Cleaning installer output directory..." -ForegroundColor Yellow
    Remove-Item -Path $installerOutputDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Ensure directories exist
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerOutputDir | Out-Null

# 2. Publish Self-Contained WinUI 3 Unpackaged App
Write-Host "[2/3] Executing dotnet publish..." -ForegroundColor Green
$dotnetArgs = @(
    "publish",
    "`"$projectFile`"",
    "-c", $Configuration,
    "-r", $Runtime,
    "-p:Platform=x64",
    "--self-contained", "true",
    "-p:WindowsPackageType=None",
    "-p:PublishSingleFile=false",
    "-o", "`"$stagingDir`""
)

$publishCmd = "dotnet " + ($dotnetArgs -join " ")
Write-Host "Running: $publishCmd" -ForegroundColor Gray
$process = Start-Process -FilePath "dotnet" -ArgumentList ($dotnetArgs -join " ") -Wait -NoNewWindow -PassThru

if ($process.ExitCode -ne 0) {
    Write-Host "Error: dotnet publish failed with exit code $($process.ExitCode)." -ForegroundColor Red
    exit $process.ExitCode
}

Write-Host "Publish completed successfully. Staged output ready at: $stagingDir" -ForegroundColor Green

# Clean up unused WinUI 3 native language resource folders (e.g. af-ZA, de-DE, fr-FR, etc.)
Write-Host "Trimming unused language resource directories from staging..." -ForegroundColor Yellow
$keepLanguages = @("en-us", "en", "en-GB")
Get-ChildItem -Path $stagingDir -Directory | Where-Object {
    $dirName = $_.Name
    # Match standard language culture folder patterns (e.g. de-DE, zh-CN, fr-FR)
    if ($dirName -match '^[a-z]{2}(-[A-Za-z0-9]+)*$' -and $keepLanguages -notcontains $dirName) {
        Remove-Item -Path $_.FullName -Recurse -Force
    }
}
Write-Host "Language trimming complete. Kept cultures: $($keepLanguages -join ', ')" -ForegroundColor Cyan
Write-Host ""

# 3. Compile Inno Setup Script
if ($SkipInstaller) {
    Write-Host "[3/3] Installer generation skipped by user flag." -ForegroundColor Yellow
    exit 0
}

Write-Host "[3/3] Locating Inno Setup Compiler (ISCC.exe)..." -ForegroundColor Green

$isccPath = $null

# Search PATH
$cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($cmd) {
    $isccPath = $cmd.Source
} else {
    # Check default installation locations
    $candidatePaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $candidatePaths) {
        if (Test-Path $path) {
            $isccPath = $path
            break
        }
    }
}

if ($isccPath) {
    Write-Host "Found Inno Setup Compiler at: $isccPath" -ForegroundColor Cyan
    Write-Host "Compiling setup executable using $installerIss..." -ForegroundColor Green

    $isccArgs = "`"$installerIss`""
    $isccProcess = Start-Process -FilePath $isccPath -ArgumentList $isccArgs -Wait -NoNewWindow -PassThru

    if ($isccProcess.ExitCode -eq 0) {
        $setupFile = Join-Path $installerOutputDir "RhythmHubSetup.exe"
        if (Test-Path $setupFile) {
            $fileSizeMB = [math]::Round((Get-Item $setupFile).Length / 1MB, 2)
            Write-Host ""
            Write-Host "============================================================" -ForegroundColor Green
            Write-Host " SUCCESS: Setup Installer Generated!" -ForegroundColor Green
            Write-Host " File: $setupFile ($fileSizeMB MB)" -ForegroundColor Green
            Write-Host "============================================================" -ForegroundColor Green
        } else {
            Write-Host "Warning: ISCC exited cleanly, but $setupFile was not found." -ForegroundColor Yellow
        }
    } else {
        Write-Host "Error: ISCC compilation failed with exit code $($isccProcess.ExitCode)." -ForegroundColor Red
        exit $isccProcess.ExitCode
    }
} else {
    Write-Host "------------------------------------------------------------" -ForegroundColor Yellow
    Write-Host " NOTICE: Inno Setup Compiler (ISCC.exe) was not found." -ForegroundColor Yellow
    Write-Host " Staged app binaries are compiled and ready in: dist\staged\" -ForegroundColor Yellow
    Write-Host ""
    Write-Host " To compile the installer executable (RhythmHubSetup.exe):" -ForegroundColor Cyan
    Write-Host " 1. Install Inno Setup via Winget:" -ForegroundColor White
    Write-Host "    winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements" -ForegroundColor BrightWhite
    Write-Host " 2. Re-run this script: .\build-and-publish.ps1" -ForegroundColor White
    Write-Host "------------------------------------------------------------" -ForegroundColor Yellow

    if ($env:CI -eq "true" -or $env:GITHUB_ACTIONS -eq "true") {
        Write-Host "Error: ISCC.exe is required in CI environment." -ForegroundColor Red
        exit 1
    }
}
