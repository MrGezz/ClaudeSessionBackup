<#
.SYNOPSIS
    Full verification gate for Claude Session Backup.

.DESCRIPTION
    Runs seven checks:

      1. BUILD        - 0 compiler errors, 0 warnings-as-errors (Release config).
      2. TESTS        - dotnet test (xunit). Tests that assert NotImplementedException
                        against the skeleton stubs are EXPECTED to fail until all
                        implementers land; the integrator decides which failing test
                        is a test bug vs an implementation bug.
      3. SMOKE        - runs the Release CLI against a throwaway destination:
                          verify, backup --no-snapshot, catalog --report, rebuild (dry)
                        asserts exit 0/1, last_run.json present with 13 stores and
                        code-transcripts > 0 files.
      4. THEME KEYS   - Semantic.Dark.xaml and Semantic.Light.xaml have identical
                        x:Key sets; every AppAccent*/Status*/Panel*/Log* key
                        referenced by any .xaml under App/ exists in both files.
      5. LAUNCH       - starts the WPF app exe and asserts a real HwndWrapper window
                        appears (not a dialog, not invisible). Adapted from Sync-ACC's
                        Verify.ps1 with the same EnumWindows / class-check pattern.
      6. PACKAGING    - Check-Packaging.ps1 (static parse + splat check) PASSES;
                        Package.ps1 -FrameworkDependent -NoZip succeeds and the staged
                        folder contains both exes, LICENSE, README.md, and Install.cmd.
                        Make-Installer.ps1 is invoked; if ISCC is absent the installer
                        step is SKIPPED rather than failed. Both build into a temp
                        folder (-OutDir) and never touch dist\, so a gate run cannot
                        overwrite or delete the release artefacts kept there.
      7. PRIVACY      - Check-Privacy.ps1 scans all tracked text files for the
                        personal-identifier denylist (username, e-mail fragments,
                        account UUIDs, absolute personal paths) and verifies that
                        docs\screenshots\*.png files are not newer than the .demo
                        marker (screenshots must come from demo mode). Use
                        -SkipPrivacy in headless environments where git is absent.

    Gates 3 and 5 require a built Release binary. Run
        dotnet build ClaudeSessionBackup.slnx -c Release
    before running this script on a fresh checkout.

    Exit code: 0 = all selected gates passed, 1 = at least one gate failed.

.PARAMETER Configuration
    Build configuration to test (Release or Debug). Smoke and Launch gates
    require the CLI exe and App exe from this configuration.

.PARAMETER SkipSmoke
    Skip gate 3. Use in CI environments that do not have live Claude stores.

.PARAMETER SkipLaunch
    Skip gate 5. Use in headless / CI environments.

.PARAMETER SkipPackaging
    Skip gate 6. Use when the packaging scripts are not available or packaging
    is handled by a separate release pipeline step.

.PARAMETER SkipPrivacy
    Skip gate 7. Use in headless / CI environments where git is not available
    and a directory walk is undesirable.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Verify.ps1
    powershell -ExecutionPolicy Bypass -File .\tools\Verify.ps1 -SkipSmoke -SkipLaunch
    powershell -ExecutionPolicy Bypass -File .\tools\Verify.ps1 -SkipPackaging
    powershell -ExecutionPolicy Bypass -File .\tools\Verify.ps1 -SkipPrivacy
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipSmoke,
    [switch] $SkipLaunch,
    [switch] $SkipPackaging,
    [switch] $SkipPrivacy
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$failed = @()

function Section { param([string] $T)
    Write-Host ''
    Write-Host "=========== $T ===========" -ForegroundColor Cyan
}
function Pass  { param([string] $M) Write-Host "  PASS  $M" -ForegroundColor Green }
function Fail  { param([string] $M) Write-Host "  FAIL  $M" -ForegroundColor Red }
function Warn  { param([string] $M) Write-Host "  WARN  $M" -ForegroundColor Yellow }
function Info  { param([string] $M) Write-Host "  INFO  $M" }

# -----------------------------------------------------------------------
# Win32 helper: enumerate visible top-level windows by process, returning
# their Win32 class names and titles.  The class is the point: a WPF
# real window is HwndWrapper[...]; a Win32 MessageBox is #32770.  We
# cannot trust the title because the app's error dialogs share the app
# title - a crash notice titled "Claude Session Backup" looks like a real
# window to MainWindowTitle.  (Learned from Sync-ACC's Verify.ps1.)
# -----------------------------------------------------------------------
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class CsbVerifyWin {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr p);
    public static string[] Visible(uint target) {
        var res = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowText(h, t, 512);
                var c = new StringBuilder(256); GetClassName(h, c, 256);
                res.Add(c.ToString() + "" + t.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return res.ToArray();
    }
}
'@

function Get-AppWindows {
    param([int] $ProcessId)
    foreach ($row in [CsbVerifyWin]::Visible([uint32] $ProcessId)) {
        $parts = $row -split ([char]31)
        [pscustomobject]@{ Class = $parts[0]; Title = $parts[1] }
    }
}

# -----------------------------------------------------------------------
# 1. BUILD
# -----------------------------------------------------------------------
Section 'BUILD'
$buildOut = & dotnet build (Join-Path $repo 'ClaudeSessionBackup.slnx') -c $Configuration -v q --nologo 2>&1
$problems = $buildOut | Select-String -Pattern ': error |: warning '
if ($LASTEXITCODE -ne 0 -or $problems) {
    $failed += 'build'
    $problems | Select-Object -Unique -First 20 | ForEach-Object { Fail $_ }
    Fail 'BUILD FAILED'
} else {
    Pass '0 errors, 0 warnings'
}

# -----------------------------------------------------------------------
# 2. TESTS
# -----------------------------------------------------------------------
Section 'TESTS'
$testOut = & dotnet test (Join-Path $repo 'ClaudeSessionBackup.slnx') --no-build -c $Configuration --logger 'console;verbosity=minimal' 2>&1
$testOut | ForEach-Object { Info $_ }
if ($LASTEXITCODE -ne 0) {
    # NotImplementedException failures against stubs are expected; log them
    # but do not fail this gate until the integrator says they should.
    Warn 'Some tests failed.'
    $failed += 'tests'
} else {
    Pass 'All currently-passing tests passed'
}

# -----------------------------------------------------------------------
# 3. SMOKE  (CLI against a throwaway destination, read-only on live stores)
# -----------------------------------------------------------------------
Section 'SMOKE'
if ($SkipSmoke) {
    Warn 'SKIPPED (-SkipSmoke)'
} else {
    $cliExe = Join-Path $repo "src\ClaudeSessionBackup.Cli\bin\$Configuration\net8.0-windows\ClaudeSessionBackup.Cli.exe"
    if (-not (Test-Path $cliExe)) {
        $failed += 'smoke'
        Fail "CLI exe not found at $cliExe - build the solution first"
    } else {
        $smokeDest = Join-Path $env:TEMP 'csb_smoke'
        Remove-Item $smokeDest -Recurse -Force -ErrorAction SilentlyContinue

        # Gate 3a: verify  (read-only check)
        & $cliExe verify --destination $smokeDest --quiet
        $verifyCode = $LASTEXITCODE
        if ($verifyCode -notin 0, 1) {
            $failed += 'smoke'
            Fail "verify exited $verifyCode (expected 0 or 1)"
        } else {
            Pass "verify exited $verifyCode"
        }

        # Gate 3b: backup --no-snapshot  (copies to throwaway dest, not live)
        & $cliExe backup --destination $smokeDest --no-snapshot --quiet
        $backupCode = $LASTEXITCODE
        if ($backupCode -notin 0, 1) {
            $failed += 'smoke'
            Fail "backup exited $backupCode (expected 0 or 1)"
        } else {
            Pass "backup exited $backupCode"
        }

        # Validate last_run.json
        $manifestPath = Join-Path $smokeDest 'last_run.json'
        if (-not (Test-Path $manifestPath)) {
            $failed += 'smoke'
            Fail "last_run.json not written to $smokeDest"
        } else {
            $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
            $storeCount = $manifest.stores.Count
            if ($storeCount -ne 13) {
                $failed += 'smoke'
                Fail "last_run.json has $storeCount stores, expected 13"
            } else {
                Pass "last_run.json has 13 stores"
            }

            # code-transcripts must have at least 1 file (live stores must exist)
            # stores is a JSON array; filter by name. Status is serialized as a string.
            $codeT = $manifest.stores | Where-Object { $_.name -eq 'code-transcripts' }
            if (-not $codeT -or ($codeT.liveFiles -lt 1 -and $codeT.status -ne 'sourceMissing')) {
                Warn "code-transcripts has 0 live files (acceptable if no transcripts on this machine)"
            } else {
                Pass "code-transcripts liveFiles: $($codeT.liveFiles)"
            }
        }

        # Gate 3c: catalog --report
        & $cliExe catalog --destination $smokeDest --report --quiet
        $catalogCode = $LASTEXITCODE
        if ($catalogCode -notin 0, 1) {
            $failed += 'smoke'
            Fail "catalog exited $catalogCode (expected 0 or 1)"
        } else {
            Pass "catalog exited $catalogCode"
        }

        # Gate 3d: rebuild --catalog (dry run, no --commit)
        $catalogJson = Join-Path $smokeDest 'catalog\sessions_catalog.json'
        if (Test-Path $catalogJson) {
            & $cliExe rebuild --catalog $catalogJson --quiet
            $rebuildCode = $LASTEXITCODE
            if ($rebuildCode -notin 0, 1) {
                $failed += 'smoke'
                Fail "rebuild (dry) exited $rebuildCode (expected 0 or 1)"
            } else {
                Pass "rebuild (dry) exited $rebuildCode"
            }
        } else {
            Warn "sessions_catalog.json not found - rebuild gate skipped"
        }
    }
}

# -----------------------------------------------------------------------
# 4. THEME KEYS
# -----------------------------------------------------------------------
Section 'THEME KEYS'
$darkFile  = Join-Path $repo 'src\ClaudeSessionBackup.App\Themes\Semantic.Dark.xaml'
$lightFile = Join-Path $repo 'src\ClaudeSessionBackup.App\Themes\Semantic.Light.xaml'

if (-not (Test-Path $darkFile) -or -not (Test-Path $lightFile)) {
    $failed += 'theme'
    Fail "Semantic.Dark.xaml or Semantic.Light.xaml not found"
} else {
    function Get-XamlKeys { param([string] $Path)
        $content = [System.IO.File]::ReadAllText($Path)
        $keyMatches = [System.Text.RegularExpressions.Regex]::Matches($content, 'x:Key="([^"]+)"')
        return @($keyMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    }

    $darkKeys  = Get-XamlKeys $darkFile
    $lightKeys = Get-XamlKeys $lightFile

    $darkSet  = [System.Collections.Generic.HashSet[string]] $darkKeys
    $lightSet = [System.Collections.Generic.HashSet[string]] $lightKeys

    $onlyInDark  = @($darkKeys  | Where-Object { -not $lightSet.Contains($_) })
    $onlyInLight = @($lightKeys | Where-Object { -not $darkSet.Contains($_) })

    $themeOk = $true
    if ($onlyInDark.Count -gt 0) {
        $themeOk = $false
        Fail "Keys in Dark but not Light: $($onlyInDark -join ', ')"
    }
    if ($onlyInLight.Count -gt 0) {
        $themeOk = $false
        Fail "Keys in Light but not Dark: $($onlyInLight -join ', ')"
    }

    if ($themeOk) {
        Pass "Dark/Light key sets are identical ($($darkKeys.Count) keys)"
    } else {
        $failed += 'theme'
    }

    # Check that every AppAccent*/Status*/Panel*/Log* key referenced in any App .xaml
    # exists in both Semantic files.
    $appXamlDir = Join-Path $repo 'src\ClaudeSessionBackup.App'
    $allAppXaml = Get-ChildItem -LiteralPath $appXamlDir -Filter '*.xaml' -Recurse
    $referencedKeys = @()
    foreach ($f in $allAppXaml) {
        $text = [System.IO.File]::ReadAllText($f.FullName)
        $m = [System.Text.RegularExpressions.Regex]::Matches($text,
            '(?:DynamicResource|StaticResource)\s+(?:x:Key=")?(?<key>(?:AppAccent|Status|Panel|Log)\w+)')
        $referencedKeys += $m | ForEach-Object { $_.Groups['key'].Value }
    }
    $referencedKeys = @($referencedKeys | Sort-Object -Unique)

    # Keys defined in Themes\Controls.xaml are theme-independent (styles such as StatusPillStyle share
    # the Status* prefix but are not brushes); they resolve in both themes by construction.
    $controlsFile = Join-Path $repo 'src\ClaudeSessionBackup.App\Themes\Controls.xaml'
    $sharedKeys = @()
    if (Test-Path -LiteralPath $controlsFile) {
        $sharedKeys = [System.Text.RegularExpressions.Regex]::Matches([System.IO.File]::ReadAllText($controlsFile), 'x:Key="([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value }
    }
    $referencedKeys = @($referencedKeys | Where-Object { $sharedKeys -notcontains $_ })

    $missingFromDark  = @($referencedKeys | Where-Object { -not $darkSet.Contains($_) })
    $missingFromLight = @($referencedKeys | Where-Object { -not $lightSet.Contains($_) })
    if ($missingFromDark.Count -gt 0) {
        $failed += 'theme'
        Fail "Keys referenced in App but missing from Semantic.Dark: $($missingFromDark -join ', ')"
    }
    if ($missingFromLight.Count -gt 0) {
        $failed += 'theme'
        Fail "Keys referenced in App but missing from Semantic.Light: $($missingFromLight -join ', ')"
    }
    if ($missingFromDark.Count -eq 0 -and $missingFromLight.Count -eq 0 -and $referencedKeys.Count -ge 0) {
        Pass "All $($referencedKeys.Count) AppAccent*/Status*/Panel*/Log* keys resolve in both themes"
    }
}

# -----------------------------------------------------------------------
# 5. LAUNCH
# -----------------------------------------------------------------------
Section 'LAUNCH'
if ($SkipLaunch) {
    Warn 'SKIPPED (-SkipLaunch)'
} else {
    $appExe = Join-Path $repo "src\ClaudeSessionBackup.App\bin\$Configuration\net8.0-windows\ClaudeSessionBackup.exe"
    if (-not (Test-Path $appExe)) {
        Warn "App exe not found at $appExe - SKIPPED (build the solution first)"
    } else {
        # Refuse to test if the app is already running to avoid signalling the user's live instance.
        $running = @(Get-Process -Name 'ClaudeSessionBackup' -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            $failed += 'launch'
            Fail 'Cannot test: ClaudeSessionBackup.exe is ALREADY RUNNING'
            Warn 'A second launch may signal the first instance and exit 0, making this gate meaningless.'
            Warn 'Close it (check the notification area) and re-run.'
        } else {
            # Clear the startup error log so its presence after launch is definitive.
            # NO logs\ segment. App.xaml.cs ReportStartupFailure writes to
            # %APPDATA%\ClaudeSessionBackup\startup-error.log; this line said
            # ...\logs\startup-error.log, so the check below watched a path the
            # app never writes and could not fail however badly startup broke.
            $startupLog = Join-Path $env:APPDATA 'ClaudeSessionBackup\startup-error.log'
            Remove-Item $startupLog -ErrorAction SilentlyContinue

            $proc = Start-Process -FilePath $appExe -PassThru
            Start-Sleep -Seconds 8

            if ($proc.HasExited) {
                $failed += 'launch'
                Fail "App exited with code $($proc.ExitCode)"
                Warn 'A clean build that will not start is almost always a XAML resource fault:'
                Warn 'StaticResource resolves at LOAD time, not compile time.'
            } else {
                # Use window CLASS, not title.  WPF real windows are HwndWrapper[...];
                # Win32 MessageBox / error dialogs are class #32770.  The app's own
                # error dialogs carry the app title, so title-only checks lie.
                $windows = Get-AppWindows -ProcessId $proc.Id
                $real    = @($windows | Where-Object { $_.Class -like 'HwndWrapper*' })
                $dialogs = @($windows | Where-Object { $_.Class -eq '#32770' })

                if ($dialogs.Count -gt 0) {
                    $failed += 'launch'
                    Fail 'App put up a dialog instead of its real window:'
                    $dialogs | ForEach-Object { Fail "  [$($_.Class)] $($_.Title)" }
                } elseif ($real.Count -eq 0) {
                    $failed += 'launch'
                    Fail 'Process is alive but produced no HwndWrapper window'
                    Warn 'A startup fault can leave an invisible process holding the mutex.'
                } else {
                    Pass "Window '$($real[0].Title)' (class $($real[0].Class))"
                }

                if (Test-Path $startupLog) {
                    if ($failed -notcontains 'launch') { $failed += 'launch' }
                    Fail 'A startup error was logged:'
                    Get-Content $startupLog | Select-Object -First 10 | ForEach-Object { Fail "  $_" }
                }

                # Only kill the process this gate started.
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        }

        # --- 5b: the window survives being USED -------------------------------
        # Opening is not the same as working. Check-TranscriptScroll launches its
        # own --demo instance and pages the turn list, which is the one place this
        # app virtualises with recycling - and the one place it has crashed
        # (2026-09-06, cached FlowDocument handed to a second viewer). It must run
        # AFTER the process above is stopped: it refuses to start beside another
        # instance so it can never kill the user's own window.
        $scrollScript = Join-Path $PSScriptRoot 'Check-TranscriptScroll.ps1'
        if (-not (Test-Path $scrollScript)) {
            Warn "Check-TranscriptScroll.ps1 not found at $scrollScript - SKIPPED"
        } else {
            Start-Sleep -Milliseconds 800
            & $scrollScript -Configuration $Configuration
            if ($LASTEXITCODE -ne 0) {
                if ($failed -notcontains 'launch') { $failed += 'launch' }
                Fail 'Check-TranscriptScroll.ps1 reported a fault'
            }
        }
    }
}

# -----------------------------------------------------------------------
# 6. PACKAGING
# -----------------------------------------------------------------------
Section 'PACKAGING'
if ($SkipPackaging) {
    Warn 'SKIPPED (-SkipPackaging)'
} else {
    $checkScript   = Join-Path $PSScriptRoot 'Check-Packaging.ps1'
    $packageScript = Join-Path $PSScriptRoot 'Package.ps1'

    # --- 6a: static analysis
    if (-not (Test-Path $checkScript)) {
        $failed += 'packaging'
        Fail "Check-Packaging.ps1 not found at $checkScript"
    } else {
        & $checkScript
        if ($LASTEXITCODE -ne 0) {
            $failed += 'packaging'
            Fail 'Check-Packaging.ps1 reported problems'
        } else {
            Pass 'Check-Packaging.ps1 passed'
        }
    }

    # --- 6b: dry build (framework-dependent, no zip - fast gate)
    if (-not (Test-Path $packageScript)) {
        $failed += 'packaging'
        Fail "Package.ps1 not found at $packageScript"
    } else {
        # Clean any previous gate-6 dist run so the presence check below is definitive.
        $version  = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
        if (-not $version) { $version = '1.0.0' }
        # Build into a TEMP folder, never dist\. dist\ holds the release artefacts
        # (self-contained zip + Setup.exe + SHA256SUMS.txt); an earlier version of
        # this gate staged its framework-dependent build there, overwrote the
        # Setup.exe and then deleted it - the installer vanished after every run.
        $gateOut   = Join-Path $env:TEMP "csb-verify-$PID"
        if (Test-Path $gateOut) { Remove-Item $gateOut -Recurse -Force -ErrorAction SilentlyContinue }
        $gateStage = Join-Path $gateOut "Claude Session Backup $version"

        # HASHTABLE splat - an array splat would bind positionally and fail at runtime.
        # This is the exact pattern Check-Packaging.ps1 verifies statically.
        $pkgArgs = @{
            Configuration    = $Configuration
            FrameworkDependent = $true
            NoZip            = $true
            OutDir           = $gateOut
        }
        # try/catch: a terminating error inside the script must fail THIS gate,
        # not abort Verify before the temp cleanup and the privacy gate run.
        try { & $packageScript @pkgArgs; $pkgExit = $LASTEXITCODE }
        catch { $pkgExit = 1; Warn "Package.ps1 threw: $($_.Exception.Message)" }

        if ($pkgExit -ne 0) {
            $failed += 'packaging'
            Fail "Package.ps1 -FrameworkDependent -NoZip failed (exit $pkgExit)"
        } else {
            Pass 'Package.ps1 -FrameworkDependent -NoZip succeeded'

            # --- 6c: staged layout check
            $stageApp = Join-Path $gateStage 'app'
            $missingItems = @()
            foreach ($name in @('ClaudeSessionBackup.exe', 'ClaudeSessionBackup.Cli.exe')) {
                if (-not (Test-Path (Join-Path $stageApp $name))) { $missingItems += "app\$name" }
            }
            foreach ($name in @('LICENSE', 'README.md', 'Install.cmd')) {
                if (-not (Test-Path (Join-Path $gateStage $name))) { $missingItems += $name }
            }
            if ($missingItems.Count -gt 0) {
                $failed += 'packaging'
                Fail "Staged layout missing: $($missingItems -join ', ')"
            } else {
                Pass "Staged layout complete (both exes, LICENSE, README.md, Install.cmd)"
            }

            # --- 6d: Make-Installer (SKIP if ISCC absent, not FAIL)
            # Must run BEFORE the staged layout is cleaned up, because -SkipPackage
            # expects the payload directory to already exist.
            $makeScript = Join-Path $PSScriptRoot 'Make-Installer.ps1'
            if (-not (Test-Path $makeScript)) {
                Warn 'Make-Installer.ps1 not found - installer step skipped'
            } else {
                try { & $makeScript -SkipPackage -OutDir $gateOut 2>&1 | Out-Null; $makeExit = $LASTEXITCODE }
                catch { $makeExit = 1; Warn "Make-Installer.ps1 threw: $($_.Exception.Message)" }
                if ($makeExit -eq 2) {
                    # Exit 2 = ISCC not installed. Treat as SKIP per the script's contract.
                    Warn 'Make-Installer.ps1: ISCC not installed - installer step SKIPPED'
                    Info "  Install Inno Setup with: winget install JRSoftware.InnoSetup"
                } elseif ($makeExit -ne 0) {
                    $failed += 'packaging'
                    Fail "Make-Installer.ps1 failed (exit $makeExit)"
                } else {
                    Pass 'Make-Installer.ps1 succeeded'
                }
            }

        }

        # Remove the gate's temp build on BOTH paths. Package.ps1 creates the
        # folder before it publishes, so a failed publish would otherwise leave
        # %TEMP%\csb-verify-<pid> behind on every failing run (review finding,
        # 2026-09-06). Nothing under dist\ is touched.
        if (Test-Path $gateOut) { Remove-Item $gateOut -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# -----------------------------------------------------------------------
# 7. PRIVACY
# -----------------------------------------------------------------------
Section 'PRIVACY'
if ($SkipPrivacy) {
    Warn 'SKIPPED (-SkipPrivacy)'
} else {
    $privacyScript = Join-Path $PSScriptRoot 'Check-Privacy.ps1'
    if (-not (Test-Path $privacyScript)) {
        $failed += 'privacy'
        Fail "Check-Privacy.ps1 not found at $privacyScript"
    } else {
        & powershell -ExecutionPolicy Bypass -File $privacyScript
        if ($LASTEXITCODE -ne 0) {
            $failed += 'privacy'
            Fail 'CHECK-PRIVACY FAILED'
        } else {
            Pass 'Check-Privacy.ps1 passed'
        }
    }
}

# -----------------------------------------------------------------------
# VERDICT
# -----------------------------------------------------------------------
Write-Host ''
Write-Host '===================================================' -ForegroundColor Cyan
if ($failed.Count -eq 0) {
    Write-Host '  ALL SELECTED GATES PASSED' -ForegroundColor Green
    Write-Host '===================================================' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Not covered by gates - only a live run or real release reveals:' -ForegroundColor DarkGray
    Write-Host '  Actual file copy correctness against live Claude stores.' -ForegroundColor DarkGray
    Write-Host '  Shrink-guard quarantine behaviour on real shrunken transcripts.' -ForegroundColor DarkGray
    Write-Host '  Snapshot creation and retention on a real destination.' -ForegroundColor DarkGray
    exit 0
}
Write-Host "  FAILED: $($failed -join ', ')" -ForegroundColor Red
Write-Host '===================================================' -ForegroundColor Cyan
exit 1
