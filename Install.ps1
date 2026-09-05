<#
    Install.ps1 - install Claude Session Backup for the current user.

    TWO MODES.

    From a packaged zip (the normal case)
        If an app\ folder is present beside this script (produced by Package.ps1),
        it is copied directly - no compiler or SDK needed. Extract the zip, run
        Install.cmd. Done.

    From the source repo
        If no app\ folder is present and the .NET SDK is available, the projects
        are published to the install folder. This is the "I have a clone and want
        to install a local build" path.

    NO ELEVATION. The app must run as the current user because their Claude stores
    live in their own profile (%APPDATA%, %LOCALAPPDATA%) and the backup destination
    is typically their own folder. Admin rights are not needed and not requested.

    SCHEDULED TASK. The -RegisterTask switch runs "ClaudeSessionBackup.Cli.exe
    install-task" after copying the files. It is OFF by default so that the first
    launch can guide the user through choosing their backup destination before the
    task fires. Use it for an automated / silent install where the destination is
    already known from a prior run's settings.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\Install.ps1
        powershell -ExecutionPolicy Bypass -File .\Install.ps1 -RegisterTask
        powershell -ExecutionPolicy Bypass -File .\Install.ps1 -InstallDir 'D:\Tools\ClaudeSessionBackup'
        powershell -ExecutionPolicy Bypass -File .\Install.ps1 -AddDesktopShortcut
#>

[CmdletBinding()]
param(
    [string] $InstallDir,
    [string] $Configuration = 'Release',
    [switch] $RegisterTask,
    [switch] $AddDesktopShortcut,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

function Write-Step { param([string] $Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn { param([string] $Text) Write-Host "    $Text" -ForegroundColor Yellow }

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeSessionBackup'
}

# A packaged build ships an app\ folder beside this script (see tools/Package.ps1).
# When that is present, copy it directly - nobody should need the SDK to install a
# backup tool.
$payloadDir  = Join-Path $repo 'app'
$fromPayload = Test-Path -LiteralPath (Join-Path $payloadDir 'ClaudeSessionBackup.exe')

Write-Host ''
Write-Host 'Claude Session Backup - install' -ForegroundColor Cyan
Write-Host '-------------------------------' -ForegroundColor Cyan

# ---------------------------------------------------------------- prerequisites
Write-Step 'Checking prerequisites'

if ($fromPayload) {
    Write-Ok 'packaged build found - no SDK needed'
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET SDK was not found on PATH. Install .NET SDK 8.0 or newer from https://dotnet.microsoft.com/download and re-run. (If you received a zip, extract it fully first - the packaged build installs without an SDK.)"
    }

    $sdks  = & dotnet --list-sdks
    $major = $sdks | ForEach-Object { ($_ -split '\.')[0] } |
             Where-Object { $_ -as [int] } | ForEach-Object { [int]$_ }
    if (-not ($major | Where-Object { $_ -ge 8 })) {
        throw "A .NET SDK 8.0 or newer is required. Found:`n$($sdks -join "`n")"
    }
    Write-Ok "dotnet $(& dotnet --version)"
}

# ---------------------------------------------------------------- build / copy
if ($fromPayload) {
    Write-Step 'Installing the packaged build'

    # Read the incoming version from the payload exe so we can warn before
    # overwriting a different installed version (especially a downgrade).
    $incomingVer = $null
    $payloadExe  = Join-Path $payloadDir 'ClaudeSessionBackup.exe'
    if (Test-Path -LiteralPath $payloadExe) {
        $incomingVer = (Get-Item -LiteralPath $payloadExe).VersionInfo.ProductVersion
        if ($incomingVer) { $incomingVer = ($incomingVer -split '[+-]')[0] }
    }

    # Remove first so a file dropped in a previous version does not survive into
    # this one and get loaded instead of its replacement.
    if (Test-Path -LiteralPath $InstallDir) {
        $installedExe = Join-Path $InstallDir 'ClaudeSessionBackup.exe'
        if ((Test-Path -LiteralPath $installedExe) -and $incomingVer) {
            $installedVer = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
            if ($installedVer) { $installedVer = ($installedVer -split '[+-]')[0] }

            if ($installedVer -and ($installedVer -ne $incomingVer)) {
                $action = 'UPGRADE'
                try {
                    if ([version]$incomingVer -lt [version]$installedVer) { $action = 'DOWNGRADE' }
                } catch { }
                Write-Host ''
                Write-Warn "Installed: $installedVer   Incoming: $incomingVer - this is a $action."
                $answer = Read-Host '    Continue? (y/N)'
                if ($answer -notmatch '^[Yy]') {
                    Write-Host '    Cancelled.' -ForegroundColor Red
                    exit 1
                }
            }
        }

        Get-Process -Name 'ClaudeSessionBackup', 'ClaudeSessionBackup.Cli' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $null = $_.CloseMainWindow()
                Start-Sleep -Milliseconds 800
                if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            }
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item (Join-Path $payloadDir '*') $InstallDir -Recurse -Force
    Write-Ok "Copied to $InstallDir"
} elseif (-not $SkipBuild) {
    Write-Step "Building ($Configuration)"

    # Both projects publish into the SAME folder: the CLI must be beside the app
    # exe because the Schedule page resolves it there. A split install would leave
    # the task pointing at nothing.
    foreach ($proj in @('src\ClaudeSessionBackup.App\ClaudeSessionBackup.App.csproj',
                        'src\ClaudeSessionBackup.Cli\ClaudeSessionBackup.Cli.csproj')) {
        & dotnet publish (Join-Path $repo $proj) -c $Configuration -o $InstallDir --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $proj (exit $LASTEXITCODE)." }
    }
    Write-Ok "Published to $InstallDir"
}

$appExe = Join-Path $InstallDir 'ClaudeSessionBackup.exe'
$cliExe = Join-Path $InstallDir 'ClaudeSessionBackup.Cli.exe'

foreach ($required in @($appExe, $cliExe)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Expected '$required' after install but it is missing."
    }
}

# ---------------------------------------------------------------- shortcuts
Write-Step 'Creating shortcuts'
$shell = New-Object -ComObject WScript.Shell

$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Session Backup.lnk'
$desktop    = Join-Path ([Environment]::GetFolderPath('Desktop'))  'Claude Session Backup.lnk'

foreach ($lnk in @($startMenu) + @(if ($AddDesktopShortcut) { $desktop })) {
    $dir = Split-Path $lnk -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath       = $appExe
    $s.WorkingDirectory = $InstallDir
    $s.Description      = 'Back up Claude Desktop and Claude Code sessions'
    $s.IconLocation     = "$appExe,0"
    $s.Save()
    Write-Ok $lnk
}

[Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

# ---------------------------------------------------------------- registry
Write-Step 'Registering for Apps & features'

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeSessionBackup'

# Stage the uninstaller BESIDE the install folder, not inside it: it cannot
# delete itself while running, and the control panel entry must keep working
# even after the user moves the repo.
$stageDir     = Split-Path $InstallDir -Parent
$uninstallPs1 = Join-Path $stageDir 'ClaudeSessionBackup-Uninstall.ps1'
$uninstallSrc = Join-Path $repo 'Uninstall.ps1'
if (Test-Path $uninstallSrc) {
    Copy-Item $uninstallSrc $uninstallPs1 -Force
}

try {
    $ver = (Get-Item -LiteralPath $appExe).VersionInfo.ProductVersion
    if (-not $ver) { $ver = '1.0.0' }
    $ver = ($ver -split '[+-]')[0]

    $sizeKb = [int](((Get-ChildItem $InstallDir -Recurse -File |
                      Measure-Object Length -Sum).Sum) / 1KB)

    if (-not (Test-Path $uninstallKey)) { New-Item -Path $uninstallKey -Force | Out-Null }

    $q        = [char]34
    $uninstArgs = '-NoProfile -ExecutionPolicy Bypass -File ' + $q + $uninstallPs1 + $q +
                  ' -InstallDir ' + $q + $InstallDir + $q

    Set-ItemProperty $uninstallKey DisplayName              'Claude Session Backup'
    Set-ItemProperty $uninstallKey DisplayVersion           $ver
    Set-ItemProperty $uninstallKey Publisher                'IcZ Workspace'
    Set-ItemProperty $uninstallKey DisplayIcon              ($q + $appExe + $q)
    Set-ItemProperty $uninstallKey InstallLocation          $InstallDir
    Set-ItemProperty $uninstallKey UninstallString          ('powershell.exe ' + $uninstArgs)
    Set-ItemProperty $uninstallKey QuietUninstallString     ('powershell.exe -NonInteractive ' + $uninstArgs)
    Set-ItemProperty $uninstallKey EstimatedSize            $sizeKb -Type DWord
    Set-ItemProperty $uninstallKey NoModify                 1       -Type DWord
    Set-ItemProperty $uninstallKey NoRepair                 1       -Type DWord

    Write-Ok "listed as 'Claude Session Backup' $ver in Apps & features"
} catch {
    Write-Warn "could not write the uninstall registry key: $($_.Exception.Message)"
}

# ---------------------------------------------------------------- scheduled task (opt-in)
if ($RegisterTask) {
    Write-Step 'Registering the backup task'
    & $cliExe install-task
    if ($LASTEXITCODE -eq 0) {
        Write-Ok 'backup task registered'
    } else {
        Write-Warn "install-task exited $LASTEXITCODE - the task was not registered"
    }
} else {
    Write-Host '    NOTE  The daily backup task was not registered (-RegisterTask to do so).' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- done
Write-Host ''
Write-Host 'Installed.' -ForegroundColor Green
Write-Host "  App : $appExe"
Write-Host "  CLI : $cliExe"
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host '  1. Launch Claude Session Backup from the Start Menu.'
Write-Host '  2. Go to Settings and choose your backup destination.'
Write-Host '  3. Run a backup once manually to confirm it works.'
Write-Host '  4. Register the scheduled task on the Schedule page (or re-run this'
Write-Host '     script with -RegisterTask) when you are satisfied with the result.'
Write-Host ''
Write-Host 'Your backup destination and settings are NEVER touched by uninstall.' -ForegroundColor DarkGray
