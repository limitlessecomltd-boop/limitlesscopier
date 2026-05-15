#requires -Version 5.0
<#
.SYNOPSIS
  Build BOTH the customer and admin installers in one go.

.DESCRIPTION
  Calls dotnet publish + Inno Setup compile for each app and reports
  both .exe paths at the end. Equivalent to running:
      .\build-installer.ps1                   (customer)
      .\build-installer.ps1 ... admin path   (admin — separate script)
  but with a single invocation, no duplicate output, and a tidy summary.

  Output:
      installer\Output\LimitlessTradeCopier-Setup-1.0.0.exe        (~150 MB)
      installer\Output\LimitlessTradeCopierAdmin-Setup-0.1.0.exe   (~150 MB)

  Total build time on a fast machine: ~3-4 minutes (both publishes,
  both LZMA2 compressions).

.PARAMETER OnlyCustomer
  Only build the customer installer. Skip the admin one.

.PARAMETER OnlyAdmin
  Only build the admin installer. Skip the customer one.

.PARAMETER SkipPublish
  Skip the dotnet publish steps. Useful when iterating on .iss scripts only.

.PARAMETER InnoSetupPath
  Path to iscc.exe. Auto-detected from standard install locations.

.EXAMPLE
  .\installer\build-all-installers.ps1

.EXAMPLE
  # Re-run only Inno Setup (after editing .iss files):
  .\installer\build-all-installers.ps1 -SkipPublish

.EXAMPLE
  # Build only the admin installer (e.g. you only changed admin code):
  .\installer\build-all-installers.ps1 -OnlyAdmin
#>

param(
    [switch]$OnlyCustomer,
    [switch]$OnlyAdmin,
    [switch]$SkipPublish,
    [string]$InnoSetupPath
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent $scriptRoot
$outputDir  = Join-Path $scriptRoot "Output"

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " Limitless Trade Copier - Multi-Installer Build" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# Locate iscc.exe once, share between both builds.
# ---------------------------------------------------------------------------
function Find-IsccExe {
    param([string]$Override)
    if ($Override) { return $Override }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
        "${env:ProgramFiles}\Inno Setup 6\iscc.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\iscc.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$iscc = Find-IsccExe -Override $InnoSetupPath
if (-not $iscc -or -not (Test-Path $iscc)) {
    $msg  = "Inno Setup not found.`n`n"
    $msg += "Install it from https://jrsoftware.org/isdl.php (free, unicode build)`n"
    $msg += "and re-run this script. Or pass -InnoSetupPath 'C:\path\to\iscc.exe'."
    throw $msg
}
Write-Host "Inno Setup: $iscc"
Write-Host ""

# ---------------------------------------------------------------------------
# Build one app: publish then iscc compile.
# Encapsulates the per-app logic so we can call it twice cleanly.
# ---------------------------------------------------------------------------
function Build-Installer {
    param(
        [string]$DisplayName,
        [string]$CsprojPath,
        [string]$PublishDir,
        [string]$IssScript,
        [string]$SanityCheckFile  # a file we expect in the publish output
    )

    Write-Host "----------------------------------------------------------------" -ForegroundColor Yellow
    Write-Host " Building: $DisplayName" -ForegroundColor Yellow
    Write-Host "----------------------------------------------------------------" -ForegroundColor Yellow

    if (-not $SkipPublish) {
        Write-Host "  [publish] dotnet publish $CsprojPath ..."
        if (Test-Path $PublishDir) {
            Write-Host "  [publish] cleaning previous publish output..."
            Remove-Item -Recurse -Force $PublishDir
        }

        & dotnet publish $CsprojPath `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:PublishReadyToRun=true `
            --nologo `
            --verbosity quiet

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $DisplayName (exit $LASTEXITCODE)"
        }

        if (-not (Test-Path $PublishDir)) {
            throw "Publish output folder missing: $PublishDir"
        }

        # Sanity check — confirm the expected payload file is present.
        # For the customer app this is mtapi.mt5.dll (broker connector).
        # For the admin app it's the admin EXE itself.
        if ($SanityCheckFile) {
            $sanity = Join-Path $PublishDir $SanityCheckFile
            if (-not (Test-Path $sanity)) {
                throw "Expected file missing from publish output: $sanity"
            }
        }

        $sizeMB = [math]::Round(((Get-ChildItem -Recurse $PublishDir |
            Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        Write-Host "  [publish] done. Folder size: $sizeMB MB" -ForegroundColor Green
    }
    else {
        Write-Host "  [publish] skipped (-SkipPublish)" -ForegroundColor DarkYellow
        if (-not (Test-Path $PublishDir)) {
            throw "Publish folder missing at $PublishDir. Run once without -SkipPublish first."
        }
    }

    Write-Host "  [iscc]    compiling $IssScript ..."
    & $iscc /Q $IssScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compile failed for $DisplayName (exit $LASTEXITCODE)"
    }
    Write-Host "  [iscc]    done." -ForegroundColor Green
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Customer app
# ---------------------------------------------------------------------------
if (-not $OnlyAdmin) {
    Build-Installer `
        -DisplayName "Customer App (LTC.App)" `
        -CsprojPath  (Join-Path $repoRoot "LTC.App\LTC.App.csproj") `
        -PublishDir  (Join-Path $repoRoot "LTC.App\bin\Release\net8.0-windows\win-x64\publish") `
        -IssScript   (Join-Path $scriptRoot "limitless-installer.iss") `
        -SanityCheckFile "mtapi.mt5.dll"
}

# ---------------------------------------------------------------------------
# Admin app
# ---------------------------------------------------------------------------
if (-not $OnlyCustomer) {
    Build-Installer `
        -DisplayName "Admin App (LTC.AdminApp)" `
        -CsprojPath  (Join-Path $repoRoot "LTC.AdminApp\LTC.AdminApp.csproj") `
        -PublishDir  (Join-Path $repoRoot "LTC.AdminApp\bin\Release\net8.0-windows\win-x64\publish") `
        -IssScript   (Join-Path $scriptRoot "limitless-admin-installer.iss") `
        -SanityCheckFile "LimitlessTradeCopierAdmin.exe"
}

# ---------------------------------------------------------------------------
# Final summary
# ---------------------------------------------------------------------------
Write-Host "================================================================" -ForegroundColor Green
Write-Host " BUILD SUCCEEDED" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""

$produced = Get-ChildItem -Path $outputDir -Filter "*.exe" |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 5
foreach ($p in $produced) {
    $sizeMB = [math]::Round($p.Length / 1MB, 1)
    Write-Host "  $($p.Name) -- $sizeMB MB"
    Write-Host "    $($p.FullName)"
    Write-Host ""
}

Write-Host "  Ship the customer installer to customers."
Write-Host "  KEEP the admin installer for yourself only."
Write-Host ""
Write-Host "  Customer install path:  C:\Program Files\Limitless Trade Copier\"
Write-Host "  Admin install path:     C:\Program Files\Limitless Trade Copier Admin\"
Write-Host ""
Write-Host "  IMPORTANT: After installing the admin app, you MUST also place"
Write-Host "  keygen-private.key somewhere the admin app can read it. Default"
Write-Host "  is the install folder, but a safer location is %LOCALAPPDATA%\Limitless\"
Write-Host "  Edit the Mint tab's 'Private key path' field if you choose a non-default."
Write-Host ""
