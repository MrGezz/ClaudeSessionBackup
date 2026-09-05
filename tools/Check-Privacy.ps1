<#
.SYNOPSIS
    Gate 7 - Privacy scan: no personal identifiers in tracked text files.

.DESCRIPTION
    Scans every tracked text file (via 'git ls-files', falling back to a
    directory walk when git is unavailable) for the denylist:

        IceCreamAssasin   - Windows username / hostname
        azdismr           - e-mail prefix
        ugezz             - e-mail fragment
        3c6e5a5c          - account/org UUID
        bca84c29          - account/org UUID
        6a690f11          - account/org UUID
        C:\Users\<alpha>  - absolute personal path (not the literal placeholder
                             C:\Users\you or C:\Users\<name>)

    Allowed (not in the denylist): IcZ Workspace, MrGezz, Claude_pzs8sxrjxfjjc.

    Also verifies that docs\screenshots\.demo exists and that every
    docs\screenshots\*.png matches the SHA-256 that Capture-Screenshots.ps1 -Demo
    recorded in that marker (screenshots must come out of the app's --demo mode).

    CLAUDE.md and memory.md are private (git-ignored) and are exempt from
    both checks.

    Exit code: 0 = PASS, 1 = FAIL (any hit, or stale/absent screenshot marker).
    Output format mirrors the other Verify.ps1 gates.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Check-Privacy.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo    = Split-Path $PSScriptRoot -Parent
$failed  = $false

function Pass  { param([string] $M) Write-Host "  PASS  $M" -ForegroundColor Green }
function Fail  { param([string] $M) Write-Host "  FAIL  $M" -ForegroundColor Red;  Set-Variable -Scope 1 failed $true }
function Warn  { param([string] $M) Write-Host "  WARN  $M" -ForegroundColor Yellow }
function Info  { param([string] $M) Write-Host "  INFO  $M" }

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

# ---------------------------------------------------------------------------
# 1. Gather text files to scan
#    Prefer git ls-files (respects .gitignore / tracked set); fall back to a
#    directory walk when git is not on PATH or the repo root has no .git.
# ---------------------------------------------------------------------------
$extensions = @('.cs','.xaml','.ps1','.cmd','.iss','.md','.json','.props','.slnx','.csproj','.py','.txt')

# Private files that are git-ignored by design - exempt from the scan.
# Also exempt this script itself: it must contain the denylist patterns as
# literal strings in order to search for them, and scanning it would always fail.
$privateFiles = @('CLAUDE.md','memory.md','Check-Privacy.ps1')

$filesToScan = [System.Collections.Generic.List[string]]::new()

$gitAvailable = $false
try {
    $null = & git -C $repo rev-parse --is-inside-work-tree 2>&1
    if ($LASTEXITCODE -eq 0) { $gitAvailable = $true }
} catch { }

if ($gitAvailable) {
    # tracked + untracked-but-not-ignored
    $listed = & git -C $repo ls-files --cached --others --exclude-standard 2>&1
    foreach ($rel in $listed) {
        if (-not $rel) { continue }
        $ext = [System.IO.Path]::GetExtension($rel).ToLowerInvariant()
        if ($extensions -notcontains $ext) { continue }
        $name = [System.IO.Path]::GetFileName($rel)
        if ($privateFiles -contains $name) { continue }
        $full = Join-Path $repo $rel.Replace('/', '\')
        if (Test-Path $full) { $filesToScan.Add($full) }
    }
    Info "Using git ls-files ($($filesToScan.Count) text files listed)"
} else {
    # Fallback: directory walk excluding generated/ignored dirs
    $excludeDirs = @('bin','obj','dist','.git')
    $allFiles    = Get-ChildItem -Path $repo -Recurse -File -ErrorAction SilentlyContinue
    foreach ($f in $allFiles) {
        $parts = $f.FullName.Substring($repo.Length).Split([IO.Path]::DirectorySeparatorChar,[StringSplitOptions]::RemoveEmptyEntries)
        $skip  = $false
        foreach ($seg in $parts[0..($parts.Count - 2)]) {
            if ($excludeDirs -contains $seg) { $skip = $true; break }
        }
        if ($skip) { continue }
        $ext  = $f.Extension.ToLowerInvariant()
        if ($extensions -notcontains $ext) { continue }
        if ($privateFiles -contains $f.Name) { continue }
        $filesToScan.Add($f.FullName)
    }
    Warn "git unavailable - walked directory tree ($($filesToScan.Count) text files)"
}

# ---------------------------------------------------------------------------
# 2. Denylist scan
# ---------------------------------------------------------------------------
$denyPatterns = @(
    [regex]::new('IceCreamAssasin',        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase),
    [regex]::new('azdismr',               [System.Text.RegularExpressions.RegexOptions]::IgnoreCase),
    [regex]::new('ugezz',                 [System.Text.RegularExpressions.RegexOptions]::IgnoreCase),
    [regex]::new('3c6e5a5c',              [System.Text.RegularExpressions.RegexOptions]::IgnoreCase),
    [regex]::new('bca84c29',              [System.Text.RegularExpressions.RegexOptions]::IgnoreCase),
    [regex]::new('6a690f11',              [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
)
# Absolute personal path: C:\Users\<one-or-more-alpha-chars>  but NOT the
# literal placeholder tokens C:\Users\you or C:\Users\<name>
$absPathPattern = [regex]::new('C:\\Users\\[A-Za-z][A-Za-z0-9_.-]+', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
$absPathAllowed = @('C:\Users\you', 'C:\Users\<name>')   # case-exact allowed literals

$scanHits = [System.Collections.Generic.List[string]]::new()

foreach ($filePath in $filesToScan) {
    try {
        $lines = [System.IO.File]::ReadAllLines($filePath, [System.Text.Encoding]::UTF8)
    } catch {
        try { $lines = [System.IO.File]::ReadAllLines($filePath, [System.Text.Encoding]::Default) }
        catch { Warn "Could not read $filePath - skipped"; continue }
    }

    $lineNum = 0
    foreach ($line in $lines) {
        $lineNum++

        foreach ($pat in $denyPatterns) {
            if ($pat.IsMatch($line)) {
                $rel = $filePath.Substring($repo.Length).TrimStart('\','/')
                $scanHits.Add("${rel}:${lineNum}: $($line.Trim())")
            }
        }

        # Absolute path check - only report matches not in the allowed-literal set
        foreach ($m in $absPathPattern.Matches($line)) {
            $allowed = $false
            foreach ($a in $absPathAllowed) {
                if ($m.Value -ieq $a) { $allowed = $true; break }
            }
            if (-not $allowed) {
                $rel = $filePath.Substring($repo.Length).TrimStart('\','/')
                $scanHits.Add("${rel}:${lineNum} [abs-path]: $($line.Trim())")
            }
        }
    }
}

if ($scanHits.Count -eq 0) {
    Pass "Text files clean - no denylist hits"
} else {
    foreach ($h in $scanHits) { Fail $h }
}

# ---------------------------------------------------------------------------
# 3. Screenshot marker check
#    docs\screenshots\.demo is written by Capture-Screenshots.ps1 -Demo and holds
#    one "<sha256>  <name>.png" line per screenshot it captured. A PNG that is
#    not listed, or whose hash differs, did not come out of demo mode - FAIL.
#    Hashes, not mtimes: a git checkout stamps every file with the checkout time
#    in index order (the PNGs land AFTER the marker), so an mtime comparison
#    fails on every CI run. A marker without hash lines (the first format) falls
#    back to the mtime rule with a WARN.
# ---------------------------------------------------------------------------
$screenshotsDir = Join-Path $repo 'docs\screenshots'
$demoMarker     = Join-Path $screenshotsDir '.demo'

if (-not (Test-Path $demoMarker)) {
    Fail "docs\screenshots\.demo marker is missing - run: tools\Capture-Screenshots.ps1 -Demo"
    Warn "  (Screenshots are not yet verified as coming from demo mode.)"
} else {
    $pngs     = @(Get-ChildItem -Path $screenshotsDir -Filter '*.png' -File -ErrorAction SilentlyContinue)
    $recorded = @{}
    foreach ($line in @(Get-Content -LiteralPath $demoMarker)) {
        if ($line -match '^\s*([0-9A-Fa-f]{64})\s+(\S+\.png)\s*$') {
            $recorded[$Matches[2].ToLowerInvariant()] = $Matches[1].ToLowerInvariant()
        }
    }
    if ($recorded.Count -gt 0) {
        $bad = 0
        foreach ($png in $pngs) {
            $key    = $png.Name.ToLowerInvariant()
            $actual = Get-Sha256Hex $png.FullName
            if (-not $recorded.ContainsKey($key)) {
                Fail "docs\screenshots\$($png.Name) is not listed in the .demo marker - not captured by demo mode (run Capture-Screenshots.ps1 -Demo)"
                $bad++
            } elseif ($recorded[$key] -ne $actual) {
                Fail "docs\screenshots\$($png.Name) does not match its .demo hash - replaced after the demo capture (run Capture-Screenshots.ps1 -Demo)"
                $bad++
            }
        }
        if ($bad -eq 0) { Pass "Screenshot marker present; all $($pngs.Count) PNG(s) match their demo-capture hashes" }
    } else {
        Warn '.demo marker has no hash lines (first format) - using the mtime rule; re-run Capture-Screenshots.ps1 -Demo to upgrade it'
        $markerTime = (Get-Item $demoMarker).LastWriteTime
        $stale = @($pngs | Where-Object { $_.LastWriteTime -gt $markerTime })
        if ($stale.Count -gt 0) {
            foreach ($png in $stale) {
                Fail "docs\screenshots\$($png.Name) is newer than .demo marker (run Capture-Screenshots.ps1 -Demo)"
            }
        } else {
            Pass "Screenshot marker present; $($pngs.Count) PNG(s) are not newer than .demo"
        }
    }
}

# ---------------------------------------------------------------------------
# Verdict
# ---------------------------------------------------------------------------
Write-Host ''
if (-not $failed) {
    Write-Host '  CHECK-PRIVACY  PASS' -ForegroundColor Green
    exit 0
} else {
    Write-Host '  CHECK-PRIVACY  FAIL' -ForegroundColor Red
    exit 1
}
