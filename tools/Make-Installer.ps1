<#
    Make-Installer.ps1 - build ClaudeSessionBackup-<version>-Setup.exe.

    Package.ps1 produces a folder and a zip; this turns the folder into a single
    installer with a wizard, because "extract the zip, then find Install.cmd" is
    a step people get wrong.

    NEEDS INNO SETUP:
        winget install JRSoftware.InnoSetup

    If ISCC.exe is not found, this script prints installation instructions and
    exits 2. It does NOT throw, so Verify.ps1's packaging gate treats the
    missing-ISCC case as a SKIP rather than a failure.

    WHY INNO AND NOT THE IN-BOX TOOLS. Two in-box routes were tried in Sync-ACC
    first, and both are recorded there so nobody spends the afternoon again:
      * 7-Zip SFX needs 7zS.sfx from the *separate* "7-Zip Extra" download; the
        normal install has only 7z.sfx (plain extractor, no RunProgram support),
        so the exe extracts and installs nothing, silently.
      * IExpress packs the payload correctly but its bootstrap never ran, and it
        gives no wizard even when it would work.
    Inno is the tool this job is actually for: wizard, Apps & features entry,
    real uninstaller, upgrade-in-place, blocks on a running app.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\Make-Installer.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\Make-Installer.ps1 -SkipPackage
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [switch] $FrameworkDependent,
    [switch] $SkipPackage,
    # Where the payload is staged and Setup.exe + SHA256SUMS.txt are written.
    # Default <repo>\dist. Verify.ps1's packaging gate passes a temp folder so a
    # gate run never touches the release artefacts in dist\.
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Step { param([string] $T) Write-Host "==> $T" -ForegroundColor Cyan }
function Ok   { param([string] $T) Write-Host "    $T" -ForegroundColor Green }
function Note { param([string] $T) Write-Host "    $T" -ForegroundColor DarkGray }

# SHA-256 as lower-case hex through .NET, not Get-FileHash: inside a nested
# Windows PowerShell child (powershell -File from another powershell) the cmdlet
# came back "not recognized" on this machine, which aborted the checksum step.
function Get-Sha256Hex {
    param([string] $Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [IO.File]::OpenRead($Path)
        try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() }
        finally { $fs.Dispose() }
    } finally { $sha.Dispose() }
}

# ---------------------------------------------------------------- ISCC
# winget installs Inno per-user by default, so check LOCALAPPDATA before the
# Program Files locations an admin install would use.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    # 64-bit Inno Setup 7 installs here by default; it was missing from the original
    # Sync-ACC list, causing a false "not installed" on a machine that had exactly that.
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ''
    Write-Host '  ISCC not installed - SKIP' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  Inno Setup is needed to build Setup.exe. Install it with:' -ForegroundColor DarkGray
    Write-Host '      winget install JRSoftware.InnoSetup' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  The zip from tools\Package.ps1 is still a fully working package.' -ForegroundColor DarkGray
    # Exit 2 = "not available", not a build failure.  Verify.ps1's packaging gate
    # treats this as SKIP rather than FAIL so CI still passes without Inno Setup.
    exit 2
}

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { $version = '1.0.0' }

$distRoot = if ($OutDir) { $OutDir } else { Join-Path $repo 'dist' }
$stage    = Join-Path $distRoot "Claude Session Backup $version"
$payload = Join-Path $stage 'app'

Write-Host ''
Write-Host "Claude Session Backup - build Setup.exe $version" -ForegroundColor Cyan
Write-Host '-------------------------------------------------' -ForegroundColor Cyan
Ok "ISCC: $iscc"

# ---------------------------------------------------------------- payload
if (-not $SkipPackage) {
    Step 'Building the package'

    # HASHTABLE splat, not an array. An array splat passes elements POSITIONALLY:
    # "-Runtime" arrives as a VALUE, not a parameter name, landing on the third
    # positional slot - which does not exist because the remaining parameters are
    # switches. Identical failure on 5.1 and pwsh 7.  See Check-Packaging.ps1.
    $pkgArgs = @{
        Configuration = $Configuration
        Runtime       = $Runtime
        NoZip         = $true
    }
    if ($FrameworkDependent) { $pkgArgs['FrameworkDependent'] = $true }
    if ($OutDir)             { $pkgArgs['OutDir'] = $OutDir }

    & (Join-Path $PSScriptRoot 'Package.ps1') @pkgArgs
    if ($LASTEXITCODE -ne 0) { throw "Package.ps1 failed (exit $LASTEXITCODE)." }
}

if (-not (Test-Path (Join-Path $payload 'ClaudeSessionBackup.exe'))) {
    throw "No payload at '$payload'. Run tools\Package.ps1 first, or drop -SkipPackage."
}
Ok "payload: $payload"

# ---------------------------------------------------------------- compile
Step 'Compiling with Inno Setup'
$iss = Join-Path $PSScriptRoot 'ClaudeSessionBackup.iss'
$out = $distRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null

$log = & $iscc `
    "/DAppVersion=$version" `
    "/DPayloadDir=$payload" `
    "/DOutDir=$out" `
    $iss 2>&1

if ($LASTEXITCODE -ne 0) {
    $log | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "ISCC failed (exit $LASTEXITCODE)."
}

$setup = Join-Path $out "ClaudeSessionBackup-$version-Setup.exe"
if (-not (Test-Path $setup)) { throw "ISCC reported success but '$setup' is missing." }

$mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Ok "$setup  ($mb MB)"

# ---------------------------------------------------------------- stale-zip guard
# This script passes -NoZip, so a zip left over from an earlier Package.ps1 run
# sits in dist\ with the same version number but different bits inside. Whoever
# picks the zip then installs code nobody tested, and the version number says it
# is the same build. Cheap to detect, so detect it.
$zip = Join-Path $out "ClaudeSessionBackup-$version-$Runtime.zip"
if (Test-Path $zip) {
    $zipAge   = (Get-Item $zip).LastWriteTimeUtc
    $buildAge = (Get-Item (Join-Path $payload 'ClaudeSessionBackup.exe')).LastWriteTimeUtc
    if ($zipAge -lt $buildAge) {
        Write-Host ''
        Write-Host "  WARNING  $(Split-Path $zip -Leaf) predates this payload and is now STALE." -ForegroundColor Yellow
        Write-Host "           Same version number, different bits. Rebuild it with:" -ForegroundColor Yellow
        Write-Host "             powershell -ExecutionPolicy Bypass -File .\tools\Package.ps1" -ForegroundColor Yellow
        Write-Host "           or delete it, so nobody ships the wrong one." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- checksums
# One SHA256SUMS.txt beside the artefacts, in the format `sha256sum -c` reads:
# LF line endings, no BOM, two spaces between hash and file name. Covers the
# Setup.exe and, when it is there, the zip from the same publish.
$sumTargets = @($setup)
if (Test-Path $zip) { $sumTargets += $zip }
$sumLines = foreach ($sf in $sumTargets) {
    '{0}  {1}' -f (Get-Sha256Hex $sf), (Split-Path $sf -Leaf)
}
$sumPath = Join-Path $out 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumPath, (($sumLines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding $false))
Ok "SHA256SUMS.txt ($($sumTargets.Count) file(s))"

Write-Host ''
Write-Host "Setup.exe built - $mb MB, single file, with a wizard." -ForegroundColor Green
Write-Host ''
Note 'UNSIGNED: SmartScreen shows "Windows protected your PC" on a machine that'
Note 'has not seen this file. More info -> Run anyway.'
Note 'Tell recipients to expect it; the warning fades as the file earns reputation.'
