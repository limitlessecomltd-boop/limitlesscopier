#requires -Version 5.0
<#
.SYNOPSIS
  Build the customer app with Obfuscar applied, then wrap in an Inno
  Setup installer.

.DESCRIPTION
  Three-stage pipeline:

    1. PUBLISH:  dotnet publish LTC.App -c Release -r win-x64 --self-contained
    2. OBFUSCATE: Obfuscar runs against the published folder; obfuscated
                  copies of LTC.App.dll, LTC.Core.dll, LTC.Persistence.dll
                  replace the originals in-place. mtapi.mt5.dll, NuGet
                  packages, .NET runtime DLLs are left untouched.
    3. INSTALLER: Inno Setup wraps the result into a single .exe.

  Why obfuscate only the customer app:
    - Admin app never leaves your machine; nothing to protect
    - Tests / console / keygen are internal
    - The customer app is the binary that strangers will try to crack

  Output:
    installer\Output\LimitlessTradeCopier-Setup-1.0.0.exe  (~150 MB)

.PARAMETER SkipPublish
  Use existing publish output (faster iteration on Obfuscar config).

.PARAMETER SkipObfuscate
  Skip obfuscation. Useful if obfuscation breaks something and you need
  to ship a clean build TEMPORARILY while debugging the obfuscator config.

.PARAMETER SkipInstaller
  Stop after obfuscation. Use this to manually launch the obfuscated EXE
  for end-to-end testing before wrapping in installer.

.EXAMPLE
  .\build-obfuscated.ps1

.EXAMPLE
  # First-time setup verification: obfuscate but don't bother with installer.
  # Then cd into the publish folder and double-click LimitlessTradeCopier.exe
  # to test every UI path. If it works, run without -SkipInstaller.
  .\build-obfuscated.ps1 -SkipInstaller
#>

param(
    [switch]$SkipPublish,
    [switch]$SkipObfuscate,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = $scriptRoot
$publishDir = Join-Path $repoRoot "LTC.App\bin\Release\net8.0-windows\win-x64\publish"
$obfuscarConfig = Join-Path $repoRoot "obfuscation\Obfuscar.xml"
$obfuscarOutput = Join-Path $repoRoot "obfuscation\Output"
$installerScript = Join-Path $repoRoot "installer\limitless-installer.iss"

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " Limitless Trade Copier - Obfuscated Release Build" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

# -----------------------------------------------------------------------
# 0. Ensure Obfuscar is installed as a dotnet tool. First-time install
#    is automatic; subsequent runs skip if already present.
# -----------------------------------------------------------------------
Write-Host "[0/3] Checking Obfuscar installation..." -ForegroundColor Yellow
$obfuscarInstalled = $false
try {
    $toolList = & dotnet tool list -g 2>$null
    if ($toolList -match "obfuscar\.globaltool") {
        $obfuscarInstalled = $true
        Write-Host "      Obfuscar is installed." -ForegroundColor Green
    }
} catch { }

if (-not $obfuscarInstalled) {
    Write-Host "      Installing Obfuscar.GlobalTool from NuGet..."
    & dotnet tool install --global Obfuscar.GlobalTool
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install Obfuscar. Check internet + NuGet.org access."
    }
    Write-Host "      Installed." -ForegroundColor Green
}
Write-Host ""

# -----------------------------------------------------------------------
# 1. PUBLISH the customer app
# -----------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "[1/3] Publishing LTC.App..." -ForegroundColor Yellow
    if (Test-Path $publishDir) {
        Write-Host "      Cleaning previous publish output..."
        Remove-Item -Recurse -Force $publishDir
    }
    & dotnet publish (Join-Path $repoRoot "LTC.App\LTC.App.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        --nologo `
        --verbosity quiet
    # PublishReadyToRun=false is REQUIRED for the obfuscated build.
    # R2R precompiles managed IL into native code embedded inside the
    # same DLL, which produces a "mixed-mode" assembly. Obfuscar can't
    # rewrite mixed-mode assemblies and fails with
    #   "Error: Writing mixed-mode assemblies is not supported"
    # The csproj has PublishReadyToRun=true for the normal release path
    # (faster cold start). We override here just for the obfuscated build.
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE)"
    }
    $sz = [math]::Round(((Get-ChildItem -Recurse $publishDir | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host "      Publish complete. Folder size: $sz MB" -ForegroundColor Green
} else {
    Write-Host "[1/3] Skipped publish (-SkipPublish)." -ForegroundColor DarkYellow
    if (-not (Test-Path $publishDir)) {
        throw "Publish folder missing at $publishDir. Run once without -SkipPublish first."
    }
}
Write-Host ""

# -----------------------------------------------------------------------
# 2. OBFUSCATE - run Obfuscar against the publish folder, replacing
#    the three of-interest DLLs in-place.
# -----------------------------------------------------------------------
if (-not $SkipObfuscate) {
    Write-Host "[2/3] Running Obfuscar..." -ForegroundColor Yellow

    # Clear previous output
    if (Test-Path $obfuscarOutput) {
        Remove-Item -Recurse -Force $obfuscarOutput
    }
    New-Item -ItemType Directory -Path $obfuscarOutput -Force | Out-Null

    # Sanity: verify the input DLLs exist before invoking Obfuscar
    $targets = @(
        (Join-Path $publishDir "LimitlessTradeCopier.dll"),
        (Join-Path $publishDir "LTC.Core.dll"),
        (Join-Path $publishDir "LTC.Persistence.dll")
    )
    foreach ($t in $targets) {
        if (-not (Test-Path $t)) {
            throw "Expected input DLL missing: $t.  Did publish succeed?"
        }
    }
    Write-Host "      Inputs found. Generating runtime config with absolute paths..."

    # Obfuscar resolves the InPath variable relative to its working directory,
    # NOT relative to the config file's location. To avoid this footgun, we
    # rewrite the InPath and OutPath variables in a temp copy of the config
    # with already-resolved absolute paths, and run Obfuscar against that.
    # The human-readable Obfuscar.xml stays clean.
    $configContent = Get-Content -Raw $obfuscarConfig
    $resolvedInPath  = (Resolve-Path $publishDir).Path
    $resolvedOutPath = (Resolve-Path $obfuscarOutput).Path
    # Escape backslashes for the regex replacement target.
    $configContent = $configContent -replace `
        '<Var name="InPath" value="[^"]*" />', `
        ('<Var name="InPath" value="' + $resolvedInPath.Replace('\', '\\') + '" />')
    $configContent = $configContent -replace `
        '<Var name="OutPath" value="[^"]*" />', `
        ('<Var name="OutPath" value="' + $resolvedOutPath.Replace('\', '\\') + '" />')
    $tempConfig = Join-Path $obfuscarOutput "Obfuscar.runtime.xml"
    Set-Content -Path $tempConfig -Value $configContent -Encoding UTF8
    Write-Host "      Wrote $tempConfig"
    Write-Host "      Invoking obfuscar.console..."

    & obfuscar.console $tempConfig
    if ($LASTEXITCODE -ne 0) {
        throw "Obfuscar failed (exit $LASTEXITCODE). Check obfuscation\Output\obfuscation-log.xml for details."
    }

    # Replace the originals in the publish folder with the obfuscated copies.
    # Anything in obfuscation\Output\ that has the same name as a publish-folder
    # DLL gets copied over. The .NET runtime DLLs, mtapi.mt5.dll, and NuGet
    # package DLLs are NOT touched.
    $replaced = 0
    Get-ChildItem -Path $obfuscarOutput -Filter "*.dll" | ForEach-Object {
        $target = Join-Path $publishDir $_.Name
        if (Test-Path $target) {
            Copy-Item -Path $_.FullName -Destination $target -Force
            $replaced++
            Write-Host "      Replaced: $($_.Name)"
        }
    }
    if ($replaced -lt 3) {
        Write-Warning "Only $replaced of 3 expected DLLs were obfuscated. Check obfuscation\Output\."
    }
    Write-Host "      Obfuscation complete." -ForegroundColor Green
} else {
    Write-Host "[2/3] Skipped obfuscation (-SkipObfuscate)." -ForegroundColor DarkYellow
}
Write-Host ""

# -----------------------------------------------------------------------
# 3. INSTALLER - run Inno Setup to wrap the (now-obfuscated) publish
#    folder into a single distributable EXE.
# -----------------------------------------------------------------------
if (-not $SkipInstaller) {
    Write-Host "[3/3] Building installer with Inno Setup..." -ForegroundColor Yellow

    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
        "${env:ProgramFiles}\Inno Setup 6\iscc.exe"
    )
    $iscc = $null
    foreach ($c in $isccCandidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }
    if (-not $iscc) {
        throw "Inno Setup not found. Install from https://jrsoftware.org/isdl.php"
    }

    & $iscc /Q $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compile failed (exit $LASTEXITCODE)."
    }

    $produced = Get-ChildItem -Path (Join-Path $repoRoot "installer\Output") -Filter "LimitlessTradeCopier-Setup-*.exe" |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
    if ($produced) {
        $sz = [math]::Round($produced.Length / 1MB, 1)
        Write-Host "      Built: $($produced.Name) ($sz MB)" -ForegroundColor Green
        Write-Host "      Path:  $($produced.FullName)"
    }
} else {
    Write-Host "[3/3] Skipped installer (-SkipInstaller)." -ForegroundColor DarkYellow
    Write-Host ""
    Write-Host "      You can now test the obfuscated build manually:" -ForegroundColor Cyan
    Write-Host "        cd $publishDir"
    Write-Host "        .\LimitlessTradeCopier.exe"
    Write-Host ""
    Write-Host "      Walk through EVERY UI path. If anything throws an error,"
    Write-Host "      especially a XAMLParseException or 'method not found',"
    Write-Host "      widen the exclusions in obfuscation\Obfuscar.xml."
}

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " BUILD SUCCEEDED" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  IMPORTANT: An obfuscated build MUST be tested end-to-end"
Write-Host "  before you ship it to customers. Run through:"
Write-Host "    1. First-run license dialog -> install a .lic file"
Write-Host "    2. Add a master account (try MT5 connection)"
Write-Host "    3. Add a slave account"
Write-Host "    4. Create a copy link"
Write-Host "    5. Edit account by double-click + right-click"
Write-Host "    6. Open Settings; toggle dark/light theme"
Write-Host "    7. Quit and reopen - license should persist"
Write-Host ""
Write-Host "  If anything errors out, look in obfuscation\Output\obfuscation-log.xml"
Write-Host "  to see which type/method triggered the problem, and add a SkipType"
Write-Host "  entry in obfuscation\Obfuscar.xml."
Write-Host ""
