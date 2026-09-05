<#
    Release.ps1 - bump version numbers and print the git commands for a release.

    Updates Directory.Build.props (<Version>, <AssemblyVersion>, <FileVersion>),
    the .iss script (AppVersion default, PayloadDir default), and README.md
    (zip/installer filenames and staged-directory names that embed the version).
    Requires that RELEASE_NOTES.md already has a heading for the target version.

    It never commits or tags on its own: it prints the exact commands and runs
    them only after an explicit y/N prompt. -NoGit skips the prompt entirely.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\Release.ps1 -Version 1.1.0
        powershell -ExecutionPolicy Bypass -File .\tools\Release.ps1 -Version 1.1.0 -NoGit
        powershell -ExecutionPolicy Bypass -File .\tools\Release.ps1 -Version 1.1.0 -AllowDirty
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $AllowDirty,
    [switch] $NoGit
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Step  { param([string] $T) Write-Host "==> $T" -ForegroundColor Cyan }
function Ok    { param([string] $T) Write-Host "    $T" -ForegroundColor Green }
function Note  { param([string] $T) Write-Host "    $T" -ForegroundColor DarkGray }

# ---------------------------------------------------------------- dirty check
if (-not $AllowDirty) {
    Push-Location $repo
    $dirty = @(git status --porcelain 2>$null)
    Pop-Location
    if ($dirty.Count -gt 0) {
        Write-Host "The working tree has uncommitted changes. Commit or stash them first," -ForegroundColor Red
        Write-Host "or pass -AllowDirty to bump anyway." -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------- read current
$propsPath = Join-Path $repo 'Directory.Build.props'
$propsXml  = [xml](Get-Content $propsPath -Raw)
$oldVersion = $propsXml.Project.PropertyGroup.Version
if (-not $oldVersion) { $oldVersion = '0.0.0' }

if ($Version -eq $oldVersion) {
    Write-Host "Version is already $Version - nothing to do." -ForegroundColor Yellow
    exit 0
}

Write-Host ''
Write-Host "Claude Session Backup - bump $oldVersion -> $Version" -ForegroundColor Cyan
Write-Host '------------------------------------------------------' -ForegroundColor Cyan

$fourPart    = "$Version.0"
$oldFourPart = "$oldVersion.0"

# ---------------------------------------------------------------- RELEASE_NOTES check
Step 'Checking RELEASE_NOTES.md'
$rnPath = Join-Path $repo 'RELEASE_NOTES.md'
$heading = "# Claude Session Backup $Version"
if (-not (Test-Path $rnPath)) {
    Write-Host "RELEASE_NOTES.md does not exist. Create it with this heading before bumping:" -ForegroundColor Red
    Write-Host "    $heading" -ForegroundColor Yellow
    exit 1
}
$rnContent = Get-Content $rnPath -Raw
if ($rnContent -notmatch [regex]::Escape($heading)) {
    Write-Host "RELEASE_NOTES.md has no heading for $Version. Add this line before bumping:" -ForegroundColor Red
    Write-Host "    $heading" -ForegroundColor Yellow
    exit 1
}
Ok "Found heading: $heading"

# ---------------------------------------------------------------- helpers
# Preserve a file's encoding (BOM) and line endings. PowerShell's Set-Content
# normalises both; this reads the raw bytes, patches, and writes them back.
function Patch-FileContent {
    param(
        [string] $Path,
        [string] $Find,
        [string] $Replace
    )
    $bytes   = [System.IO.File]::ReadAllBytes($Path)
    # Skip BOM bytes (EF BB BF) so the in-memory string is BOM-free;
    # WriteAllText with the BOM encoder then adds exactly one BOM.
    $start  = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    $raw    = [System.Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start)
    $patched = $raw.Replace($Find, $Replace)
    if ($patched -eq $raw) { return $false }

    $enc    = if ($start -eq 3) { [System.Text.UTF8Encoding]::new($true) } else { [System.Text.UTF8Encoding]::new($false) }
    [System.IO.File]::WriteAllText($Path, $patched, $enc)
    return $true
}

$changed = @()

# ---------------------------------------------------------------- Directory.Build.props
Step 'Bumping Directory.Build.props'
$hit = $false
$hit = $hit -bor (Patch-FileContent $propsPath "<Version>$oldVersion</Version>" "<Version>$Version</Version>")
$hit = $hit -bor (Patch-FileContent $propsPath "<AssemblyVersion>$oldFourPart</AssemblyVersion>" "<AssemblyVersion>$fourPart</AssemblyVersion>")
$hit = $hit -bor (Patch-FileContent $propsPath "<FileVersion>$oldFourPart</FileVersion>" "<FileVersion>$fourPart</FileVersion>")
if ($hit) { Ok "Directory.Build.props -> $Version"; $changed += 'Directory.Build.props' }
else      { Note "Directory.Build.props: no changes needed" }

# ---------------------------------------------------------------- .iss
Step 'Bumping ClaudeSessionBackup.iss'
$issPath = Join-Path $repo 'tools\ClaudeSessionBackup.iss'
if (Test-Path $issPath) {
    $hit = $false
    # #define AppVersion "x.y.z"
    $hit = $hit -bor (Patch-FileContent $issPath "#define AppVersion `"$oldVersion`"" "#define AppVersion `"$Version`"")
    # #define PayloadDir "..\dist\Claude Session Backup x.y.z\app"
    $hit = $hit -bor (Patch-FileContent $issPath "Claude Session Backup $oldVersion" "Claude Session Backup $Version")
    if ($hit) { Ok "ClaudeSessionBackup.iss -> $Version"; $changed += 'tools\ClaudeSessionBackup.iss' }
    else      { Note "ClaudeSessionBackup.iss: no changes needed" }
} else {
    Note 'ClaudeSessionBackup.iss not found - skipped'
}

# ---------------------------------------------------------------- README.md
Step 'Bumping README.md'
$readmePath = Join-Path $repo 'README.md'
if (Test-Path $readmePath) {
    $hit = $false
    # ClaudeSessionBackup-x.y.z- (zip and setup filenames)
    $hit = $hit -bor (Patch-FileContent $readmePath "ClaudeSessionBackup-$oldVersion-" "ClaudeSessionBackup-$Version-")
    # Claude Session Backup x.y.z (staged directory names in prose)
    $hit = $hit -bor (Patch-FileContent $readmePath "Claude Session Backup $oldVersion" "Claude Session Backup $Version")
    if ($hit) { Ok "README.md -> $Version"; $changed += 'README.md' }
    else      { Note "README.md: no changes needed" }
} else {
    Note 'README.md not found - skipped'
}

# ---------------------------------------------------------------- summary
Write-Host ''
if ($changed.Count -eq 0) {
    Write-Host "No files were changed (version strings may already be at $Version)." -ForegroundColor Yellow
    exit 0
}

Write-Host "Changed $($changed.Count) file(s): $($changed -join ', ')" -ForegroundColor Green

# ---------------------------------------------------------------- git
if ($NoGit) {
    Write-Host ''
    Note 'Git commands (not run - -NoGit):'
    Note "    git add $($changed -join ' ')"
    Note "    git commit -m `"$Version`""
    Note "    git tag v$Version"
    Note "    git push origin $(git -C $repo rev-parse --abbrev-ref HEAD 2>$null) --tags"
    exit 0
}

Write-Host ''
Write-Host 'Git commands to run:' -ForegroundColor Yellow
Write-Host "    git add $($changed -join ' ')" -ForegroundColor White
Write-Host "    git commit -m `"$Version`"" -ForegroundColor White
Write-Host "    git tag v$Version" -ForegroundColor White

Push-Location $repo
$branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
Pop-Location
if ($branch) {
    Write-Host "    git push origin $branch --tags" -ForegroundColor White
}

Write-Host ''
$answer = Read-Host 'Run these now? (y/N)'
if ($answer -ne 'y') {
    Write-Host 'Aborted. The files are bumped; run the git commands yourself.' -ForegroundColor Yellow
    exit 0
}

Push-Location $repo
git add @changed
git commit -m "$Version"
git tag "v$Version"
Pop-Location

Write-Host ''
Write-Host "Committed and tagged v$Version." -ForegroundColor Green
Note "Push with: git push origin $branch --tags"
