<#
    Uninstall.ps1 - remove Claude Session Backup from this machine.

    ORDER MATTERS. The scheduled task is unregistered FIRST, before the executable
    it points at is deleted. Doing it the other way round leaves Task Scheduler
    firing at a binary that no longer exists - it does not clean itself up, and
    the failures pile up silently in a log nobody reads.

    WHAT IS NEVER TOUCHED:
      * Anything under your backup destination. Those backed-up files are the entire
        point of the tool. This script does not read the backup destination at all,
        so it cannot delete your backups even by accident.
      * Your settings (%APPDATA%\ClaudeSessionBackup). Uninstall is very often
        "reinstall", and re-entering your backup destination and options by hand is
        a miserable way to find out otherwise.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
        powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Passed in by the Apps & features entry the installer wrote, so removal works
    # from the right place even if the user moved their install.
    [string] $InstallDir
)

$ErrorActionPreference = 'Stop'

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeSessionBackup'
}

function Step { param([string] $T) Write-Host "==> $T" -ForegroundColor Cyan }
function Ok   { param([string] $T) Write-Host "    $T" -ForegroundColor Green }
function Skip { param([string] $T) Write-Host "    $T" -ForegroundColor DarkGray }
function Warn { param([string] $T) Write-Host "    $T" -ForegroundColor Yellow }

Write-Host ''
Write-Host 'Claude Session Backup - uninstall' -ForegroundColor Cyan
Write-Host '---------------------------------' -ForegroundColor Cyan

# ---------------------------------------------------------------- 1. scheduled task
# FIRST, for the reason in the header: task before binary.
Step 'Removing scheduled tasks'

$cliExe = Join-Path $InstallDir 'ClaudeSessionBackup.Cli.exe'
$removedTask = $false

# Ask the CLI to unregister cleanly first - it knows the task name it used.
if (Test-Path -LiteralPath $cliExe) {
    if ($PSCmdlet.ShouldProcess($cliExe, 'Run uninstall-task')) {
        & $cliExe uninstall-task 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Ok 'backup task removed via CLI'; $removedTask = $true }
    }
}

# Fallback: scan Task Scheduler for anything that invokes our exe.
if (-not $removedTask) {
    try {
        Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
            foreach ($a in $_.Actions) {
                if ($a.Execute -and $a.Execute -match 'ClaudeSessionBackup\.Cli') {
                    if ($PSCmdlet.ShouldProcess($_.TaskName, 'Unregister scheduled task')) {
                        Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false
                        Ok "removed task '$($_.TaskName)'"
                        $removedTask = $true
                    }
                }
            }
        }
    } catch {
        Warn "could not enumerate scheduled tasks: $($_.Exception.Message)"
    }
}

if (-not $removedTask) { Skip 'no backup task found' }

# ---------------------------------------------------------------- 2. running processes
Step 'Closing the app if it is running'
$procs = Get-Process -Name 'ClaudeSessionBackup', 'ClaudeSessionBackup.Cli' -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        if ($PSCmdlet.ShouldProcess($p.ProcessName, 'Stop process')) {
            $null = $p.CloseMainWindow()
            Start-Sleep -Milliseconds 1200
            if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
            Ok "closed $($p.ProcessName)"
        }
    }
} else {
    Skip 'not running'
}

# ---------------------------------------------------------------- 3. shortcuts
Step 'Removing shortcuts'
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Session Backup.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop'))  'Claude Session Backup.lnk')
)
$removedLinks = 0
foreach ($lnk in $shortcuts) {
    if (Test-Path -LiteralPath $lnk) {
        if ($PSCmdlet.ShouldProcess($lnk, 'Delete shortcut')) {
            Remove-Item -LiteralPath $lnk -Force
            Ok $lnk
            $removedLinks++
        }
    }
}
if ($removedLinks -eq 0) { Skip 'none found' }

# ---------------------------------------------------------------- 4. application
Step 'Removing the application'
if (Test-Path -LiteralPath $InstallDir) {
    if ($PSCmdlet.ShouldProcess($InstallDir, 'Delete folder')) {
        try {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
            Ok $InstallDir
        } catch {
            Warn "could not fully remove $InstallDir : $($_.Exception.Message)"
            Warn 'Close anything using it and re-run.'
        }
    }
} else {
    Skip "not installed at $InstallDir"
}

# ---------------------------------------------------------------- 4b. registry
Step 'Removing the Apps & features entry'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeSessionBackup'
if (Test-Path $uninstallKey) {
    if ($PSCmdlet.ShouldProcess($uninstallKey, 'Delete registry key')) {
        try {
            Remove-Item -Path $uninstallKey -Recurse -Force
            Ok 'unlisted from Apps & features'
        } catch {
            Warn "could not remove the registry key: $($_.Exception.Message)"
        }
    }
} else {
    Skip 'not listed in Apps & features'
}

# ---------------------------------------------------------------- 5. self
# The staged copy lives beside the install folder so the registry entry keeps
# working after the repo is gone. It cannot delete itself while running, so
# hand the job to a detached cmd that waits a moment first. Best effort only.
$self     = $MyInvocation.MyCommand.Path
$stageDir = Split-Path $InstallDir -Parent
if ($self -and (Split-Path $self -Parent) -eq $stageDir -and -not $WhatIfPreference) {
    Start-Process cmd.exe -WindowStyle Hidden -ArgumentList (
        '/c timeout /t 2 /nobreak >nul & del /q ' + [char]34 + $self + [char]34) | Out-Null
}

# ---------------------------------------------------------------- done
Write-Host ''
Write-Host 'Uninstalled.' -ForegroundColor Green
Write-Host ''
Write-Host 'Left untouched, deliberately:' -ForegroundColor DarkGray
Write-Host '  Your backup destination and everything backed up to it. This script' -ForegroundColor DarkGray
Write-Host '  does not read the backup destination at all, so it cannot remove' -ForegroundColor DarkGray
Write-Host '  your backups even by accident.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Your settings (%APPDATA%\ClaudeSessionBackup), so a reinstall picks' -ForegroundColor DarkGray
Write-Host '  up your configuration straight away.' -ForegroundColor DarkGray
