#requires -Version 5.0
<#
.SYNOPSIS
  Build the Limitless Trade Copier Windows installer in one shot.

.DESCRIPTION
  Steps:
    1. dotnet publish LTC.App in Release / win-x64 / self-contained
       (drops a folder of ~150MB containing the app + .NET 8 runtime)
    2. iscc.exe limitless-installer.iss
       (wraps that folder into a single .exe installer)

  The output lands in:
    installer\Output\LimitlessTradeCopier-Setup-1.0.0.exe

  That single .exe is what you ship. Users double-click it, click Next a
  few times, app is installed.

.PARAMETER Configuration
  Build configuration. Always Release for installers. Default: Release.

.PARAMETER SkipPublish
  Skip the dotnet publish step (useful when iterating on the .iss script).
  Default: false.

.PARAMETER InnoSetupPath
  Path to Inno Setup's iscc.exe. Auto-detected from the standard install
  locations if not provided.

.EXAMPLE
  # From a Developer PowerShell window in the repo root:
  .\installer\build-installer.ps1

.EXAMPLE
  # Re-run only the Inno Setup step after editing the .iss script:
  .\installer\build-installer.ps1 -SkipPublish

.NOTES
  Requirements (one-time setup on the build machine):
    - .NET 8 SDK   (https://dotnet.microsoft.com/download/dotnet/8.0)
    - Inno Setup 6 (https://jrsoftware.org/isdl.php -- pick the unicode build)

  This script uses ASCII-only characters in all string output so it loads
  cleanly on Windows PowerShell 5.1 (the default on Windows 10/11), which
  reads .ps1 files using the system code page rather than UTF-8 by default.
#>

param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [string]$InnoSetupPath
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to THIS script's location so it works regardless of
# where the user invokes it from.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent $scriptRoot
$appProj    = Join-Path $repoRoot "LTC.App\LTC.App.csproj"
$publishDir = Join-Path $repoRoot "LTC.App\bin\$Configuration\net8.0-windows\win-x64\publish"
$issScript  = Join-Path $scriptRoot "limitless-installer.iss"
$outputDir  = Join-Path $scriptRoot "Output"

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " Limitless Trade Copier - Installer Build" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Repo root:    $repoRoot"
Write-Host "Publish dir:  $publishDir"
Write-Host "Output dir:   $outputDir"
Write-Host ""

# ----------------------------------------------------------------------------
# Step 1: dotnet publish (self-contained, win-x64, Release)
# ----------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "[1/2] Publishing app (self-contained, win-x64)..." -ForegroundColor Yellow
    Write-Host "      This bundles the .NET 8 runtime so users do not need to install it."
    Write-Host ""

    # Wipe the publish folder first so stale files from a previous build never
    # leak into the installer. Without this, removed files would still ship.
    if (Test-Path $publishDir) {
        Write-Host "      Cleaning previous publish output..."
        Remove-Item -Recurse -Force $publishDir
    }

    & dotnet publish $appProj `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    if (-not (Test-Path $publishDir)) {
        throw "Publish completed but output folder not found at $publishDir. Did the project layout change?"
    }

    # Sanity check: make sure mtapi.mt5.dll actually landed in the publish dir.
    # If it did not, the user will get cryptic "could not connect" errors at
    # runtime that are a pain to debug.
    $mtapiPath = Join-Path $publishDir "mtapi.mt5.dll"
    if (-not (Test-Path $mtapiPath)) {
        $msg  = "mtapi.mt5.dll is MISSING from the publish output.`n"
        $msg += "Expected at: $mtapiPath`n"
        $msg += "Check the LTC.App.csproj <None Include=`"..\lib\mtapi.mt5.dll`"> entry.`n"
        $msg += "The installer would compile, but the installed app would crash on first connect."
        throw $msg
    }

    $publishSize = (Get-ChildItem -Recurse $publishDir | Measure-Object -Property Length -Sum).Sum
    $publishMB = [math]::Round($publishSize / 1MB, 1)
    Write-Host ""
    Write-Host "      Published. Folder size: $publishMB MB" -ForegroundColor Green
}
else {
    Write-Host "[1/2] Skipping publish (-SkipPublish was given)" -ForegroundColor DarkYellow
    if (-not (Test-Path $publishDir)) {
        throw "Publish folder not found at $publishDir. Run without -SkipPublish first."
    }
}

# ----------------------------------------------------------------------------
# Step 2: Inno Setup compile
# ----------------------------------------------------------------------------
Write-Host ""
Write-Host "[2/2] Compiling installer with Inno Setup..." -ForegroundColor Yellow

# Locate iscc.exe. Inno Setup installs by default into Program Files (x86) on
# 64-bit Windows because it is a 32-bit app. Try the common spots in order.
if (-not $InnoSetupPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
        "${env:ProgramFiles}\Inno Setup 6\iscc.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\iscc.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $InnoSetupPath = $c; break }
    }
}

if (-not $InnoSetupPath -or -not (Test-Path $InnoSetupPath)) {
    $msg  = "Inno Setup not found.`n`n"
    $msg += "Install it from https://jrsoftware.org/isdl.php (the unicode version, free)`n"
    $msg += "and re-run this script. Or pass -InnoSetupPath 'C:\path\to\iscc.exe' if you`n"
    $msg += "have it installed somewhere unusual."
    throw $msg
}

Write-Host "      iscc:  $InnoSetupPath"

# Make sure the output folder exists; iscc creates it if the .iss says so but
# being explicit avoids surprises.
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# /Q = quiet (suppress the iscc banner), but keep error output visible.
& $InnoSetupPath /Q $issScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed with exit code $LASTEXITCODE"
}

# Find what was produced and report.
$produced = Get-ChildItem -Path $outputDir -Filter "*.exe" |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $produced) {
    throw "Inno Setup reported success but no .exe was produced in $outputDir"
}

$installerMB = [math]::Round($produced.Length / 1MB, 1)

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " BUILD SUCCEEDED" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Installer:  $($produced.FullName)"
Write-Host "  Size:       $installerMB MB"
Write-Host ""
Write-Host "  Test it:    Right-click the .exe, choose Run as administrator"
Write-Host "              (UAC prompt is normal, installer writes to Program Files.)"
Write-Host ""
Write-Host "  Ship it:    Upload that single .exe wherever you distribute the app."
Write-Host "              Users double-click it, click Next a few times, done."
Write-Host ""
