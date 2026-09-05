#Requires -Version 5.1
<#
.SYNOPSIS
    Capture the README screenshots from the app, page by page, via UI Automation.

.DESCRIPTION
    Drives ClaudeSessionBackup.exe the way a person would - the six nav items,
    the theme toggle - and grabs the window from the screen after each step.
    Output: docs\screenshots\<page>.png (dark) and dashboard-light.png.

    Why capture from the screen rather than PrintWindow: the window uses the
    Mica backdrop, which DWM composites; PrintWindow returns black where the
    backdrop should be. So the window is brought to the front, sized to a known
    rectangle, and copied from the desktop, cropped to the DWM extended frame
    (the visible edge, not the 8 px invisible border).

    -Demo (the default for screenshots destined for the public repo):
      Stops any running ClaudeSessionBackup, launches the Release exe with
      --demo (entirely fictional data), captures every page without running
      Backup/Refresh/Plan (the demo comes pre-populated), writes the
      docs\screenshots\.demo marker, then stops the demo instance and
      relaunches without --demo so the user's app comes back.

    Without -Demo the script expects the app to be already running with real
    data. It runs a real Backup and Refresh, so the pages are populated from
    the live Claude stores. Those screenshots contain real paths and session
    titles and MUST NOT be committed to the public repo.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Capture-Screenshots.ps1 -Demo
#>
[CmdletBinding()]
param(
    [switch]$Demo,
    [string]$OutDir = '',
    [int]$Width = 1280,
    [int]$Height = 820,
    [int]$SettleMs = 900
)

$ErrorActionPreference = 'Stop'
$scriptStartUtc = [DateTime]::UtcNow   # the marker lists only PNGs written after this

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
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'docs\screenshots' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -Namespace Shot -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
'@

# Per-monitor-v2 DPI awareness so every rectangle below is in physical pixels.
[void][Shot.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

$exePath = Join-Path $repoRoot 'src\ClaudeSessionBackup.App\bin\Release\net8.0-windows\ClaudeSessionBackup.exe'

# ───────────────────────────────────── Demo mode: launch with --demo ──────
if ($Demo) {
    if (-not (Test-Path $exePath)) {
        throw "Release build not found at $exePath. Run: dotnet build -c Release"
    }

    # Stop any running instance (real or demo) before launching demo.
    Get-Process ClaudeSessionBackup -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "stopping running instance (PID $($_.Id))..."
        $_.CloseMainWindow() | Out-Null
        if (-not $_.WaitForExit(5000)) { $_.Kill() }
    }
    Start-Sleep -Milliseconds 500

    Write-Host 'launching demo instance...'
    $demoProc = Start-Process -FilePath $exePath -ArgumentList '--demo' -PassThru
    # Wait for the main window to appear (up to 15 s).
    $deadline = (Get-Date).AddSeconds(15)
    while ($demoProc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $demoProc.Refresh()
    }
    if ($demoProc.MainWindowHandle -eq 0) {
        throw 'demo instance did not produce a window within 15 s'
    }
    $proc = $demoProc
} else {
    Write-Host ''
    Write-Host '  WARNING: running WITHOUT -Demo.' -ForegroundColor Red
    Write-Host '  Screenshots will contain real paths, session titles, and machine names.' -ForegroundColor Red
    Write-Host '  Do NOT commit these to the public repo.' -ForegroundColor Red
    Write-Host ''

    $proc = Get-Process ClaudeSessionBackup -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $proc) { throw 'ClaudeSessionBackup.exe is not running (or has no window yet). Start it first.' }
}

$hwnd = $proc.MainWindowHandle

# --- place the window: restored, known size, top-left of the primary screen -----
[void][Shot.Native]::ShowWindow($hwnd, 9)                    # SW_RESTORE
[void][Shot.Native]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, $Width, $Height, 0x0040)   # SWP_SHOWWINDOW
Start-Sleep -Milliseconds 400

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win  = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { throw 'UI Automation cannot see the window.' }

# ───────────────────────────────── Helper functions ───────────────────────
function Get-Elements([string]$controlType) {
    $all = $win.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    @($all | Where-Object {
        $_.Current.ControlType.ProgrammaticName -eq "ControlType.$controlType"
    })
}
function Find-Button([string]$name) {
    Get-Elements 'Button' |
        Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
}
function Find-ButtonByHelp([string]$help) {
    Get-Elements 'Button' |
        Where-Object { $_.Current.HelpText -eq $help } | Select-Object -First 1
}
function Invoke-Element($el) {
    if (-not $el) { return $false }
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke(); return $true
}
function Select-Nav([int]$index) {
    $radios = Get-Elements 'RadioButton' |
        Where-Object { $_.Current.BoundingRectangle.X -lt 220 } |
        Sort-Object { $_.Current.BoundingRectangle.Y }
    if ($radios.Count -lt $index + 1) {
        throw "expected 6 nav items, found $($radios.Count)"
    }
    $pat = $radios[$index].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
    Start-Sleep -Milliseconds $SettleMs
}
function Wait-Idle([int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    do {
        Start-Sleep -Milliseconds 500
        $cancel = Find-Button 'Cancel'
        if (-not $cancel -or -not $cancel.Current.IsEnabled) { return }
    } while ((Get-Date) -lt $deadline)
}
function Save-Shot([string]$name) {
    $win.SetFocus()
    [void][Shot.Native]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 350
    $r = New-Object Shot.Native+RECT
    [void][Shot.Native]::DwmGetWindowAttribute(
        $hwnd, 9, [ref]$r,
        [System.Runtime.InteropServices.Marshal]::SizeOf($r))
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0,
        (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("saved {0} ({1}x{2})" -f $path, $w, $h)
}

# ─────────────────────────────── Capture sequence ────────────────────────

if ($Demo) {
    # Demo mode: data is pre-populated; just navigate and capture.

    # Dashboard (already the active page)
    Select-Nav 0
    Start-Sleep -Milliseconds $SettleMs
    Save-Shot 'dashboard'

    # Catalog
    Select-Nav 1
    Save-Shot 'catalog'

    # Restore
    Select-Nav 2
    Save-Shot 'restore'

    # Transcript: already loaded in demo mode; just navigate to the tab.
    Select-Nav 5
    Start-Sleep -Milliseconds $SettleMs
    Save-Shot 'transcript'

    # Schedule
    Select-Nav 3
    Save-Shot 'schedule'

    # Settings
    Select-Nav 4
    Save-Shot 'settings'

    # Light theme: toggle, capture dashboard, toggle back
    $toggle = Find-ButtonByHelp 'Toggle light / dark theme'
    if (Invoke-Element $toggle) {
        Start-Sleep -Milliseconds $SettleMs
        Select-Nav 0
        Save-Shot 'dashboard-light'
        [void](Invoke-Element (Find-ButtonByHelp 'Toggle light / dark theme'))
        Start-Sleep -Milliseconds 400
    }

    # Write the .demo marker: a UTC timestamp line, then one "<sha256>  <name>"
    # line per PNG this run wrote. Check-Privacy.ps1 compares hashes, not mtimes
    # (a git checkout re-stamps every file, so mtimes prove nothing on CI). Only
    # files written since this script started are listed: a stray non-demo PNG
    # left in the folder is never blessed by a later demo run.
    $markerPath  = Join-Path $OutDir '.demo'
    $markerLines = @("$((Get-Date).ToUniversalTime().ToString('o')) demo")
    $freshPngs   = @(Get-ChildItem -Path $OutDir -Filter '*.png' -File |
                     Where-Object { $_.LastWriteTimeUtc -ge $scriptStartUtc } | Sort-Object Name)
    foreach ($png in $freshPngs) {
        $markerLines += ('{0}  {1}' -f (Get-Sha256Hex $png.FullName), $png.Name)
    }
    [IO.File]::WriteAllText($markerPath, (($markerLines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding $false))
    Write-Host "marker: $($freshPngs.Count) screenshot hash(es) recorded in .demo"

    # Stop the demo instance, relaunch the real app so the user's window comes back.
    Write-Host 'stopping demo instance...'
    $demoProc.CloseMainWindow() | Out-Null
    if (-not $demoProc.WaitForExit(5000)) { $demoProc.Kill() }

    Write-Host 'relaunching without --demo...'
    Start-Process -FilePath $exePath

} else {
    # Real mode: run Backup, Refresh, Plan to populate the pages first.

    # Dashboard: run a real Backup
    Select-Nav 0
    if (Invoke-Element (Find-Button 'Backup now')) {
        Start-Sleep -Milliseconds 800
        Wait-Idle 300
        Start-Sleep -Milliseconds $SettleMs
    }
    Save-Shot 'dashboard'

    # Catalog: Refresh builds the catalog from live stores
    Select-Nav 1
    if (Invoke-Element (Find-Button 'Refresh')) { Start-Sleep -Seconds 12 }
    Save-Shot 'catalog'

    # Restore: plan the sidebar rebuild (dry run)
    Select-Nav 2
    $plan = Get-Elements 'Button' |
        Where-Object { $_.Current.Name -like 'Plan*' } | Select-Object -First 1
    if (Invoke-Element $plan) { Start-Sleep -Seconds 4 }
    Save-Shot 'restore'

    # Transcript: double-click the first catalog row
    Select-Nav 1
    Start-Sleep -Milliseconds $SettleMs
    $transcriptCaptured = $false
    $dgRows = Get-Elements 'DataItem'
    if ($dgRows.Count -gt 0) {
        try {
            $selPat = $dgRows[0].GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat.Select()
        } catch { }
        Start-Sleep -Milliseconds 300
        $rect = $dgRows[0].Current.BoundingRectangle
        $cx = [int]($rect.Left + $rect.Width / 2)
        $cy = [int]($rect.Top + $rect.Height / 2)
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class ClickHelper {
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04;
    public static void DoubleClick(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(50);
        var down = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        var up   = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
        System.Threading.Thread.Sleep(60);
        SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@ -ErrorAction SilentlyContinue
        [void][Shot.Native]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 200
        [ClickHelper]::DoubleClick($cx, $cy)
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 1000
            $radios = Get-Elements 'RadioButton' |
                Where-Object { $_.Current.BoundingRectangle.X -lt 220 } |
                Sort-Object { $_.Current.BoundingRectangle.Y }
            $transcriptRadio = $radios | Select-Object -Last 1
            if ($transcriptRadio -and $transcriptRadio.Current.IsEnabled) { break }
        } while ((Get-Date) -lt $deadline)
        Start-Sleep -Milliseconds $SettleMs
        Select-Nav 5
        Save-Shot 'transcript'
        $transcriptCaptured = $true
    }
    if (-not $transcriptCaptured) {
        Write-Host 'WARNING: could not open a transcript - transcript screenshot skipped'
    }

    # Schedule and Settings
    Select-Nav 3; Save-Shot 'schedule'
    Select-Nav 4; Save-Shot 'settings'

    # Light theme
    $toggle = Find-ButtonByHelp 'Toggle light / dark theme'
    if (Invoke-Element $toggle) {
        Start-Sleep -Milliseconds $SettleMs
        Select-Nav 0
        Save-Shot 'dashboard-light'
        [void](Invoke-Element (Find-ButtonByHelp 'Toggle light / dark theme'))
        Start-Sleep -Milliseconds 400
    }
}

Write-Host "done: $OutDir"
