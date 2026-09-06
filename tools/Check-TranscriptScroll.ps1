#Requires -Version 5.1
<#
.SYNOPSIS
    Drives the Transcript page in demo mode and fails if the app raises an error
    dialog while the turn list is scrolled.

.DESCRIPTION
    The launch gate proves the window opens. It cannot prove the window survives
    being USED, and the transcript is where this app does its only non-trivial
    virtualisation: a recycling ListBox whose items each host a
    FlowDocumentScrollViewer.

    That combination has one sharp edge. A FlowDocument belongs to exactly one
    viewer; recycling drops a container without clearing its Document, so any
    per-block CACHED document is still owned by the discarded viewer when the
    block scrolls back into view, and the second viewer throws
    "Document belongs to another FlowDocumentScrollViewer already" - surfaced as
    XamlParseException from inside the DataTemplate (2026-09-06). Views\MarkdownHost.cs
    is what stops it; this is what proves it stays stopped.

    Method, and why each part is the way it is:

      * --demo, so nothing real is read and the transcript is deterministic.
      * The turn list is paged through its UI Automation ScrollPattern, NOT the
        mouse wheel. The cursor sits over a FlowDocumentScrollViewer, which
        handles the wheel itself, so wheel events never reach the list: a
        wheel-driven version of this check PASSED against a deliberately broken
        build because it had scrolled nothing.
      * VerticalScrollPercent is sampled and the run fails if the list barely
        moved, so a silent no-op cannot masquerade as a pass.
      * Dialogs are found by enumerating every top-level window the process owns
        and matching class #32770. Process.MainWindowHandle keeps pointing at the
        real WPF window when a MessageBox is up, which is the second way an
        earlier version of this check passed against a build that was throwing.

    Verified A/B on 2026-09-07: fails in the first cycle against the pre-fix
    cached-FlowDocument build, passes 120 page-scrolls against the fix.

.PARAMETER Configuration
    Which build to drive. Must match the one Verify.ps1 tested.

.PARAMETER Cycles
    Down-and-up passes over the turn list. Five is enough to recycle every
    container in the demo transcript several times over.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\Check-TranscriptScroll.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$Cycles = 5
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $repo "src\ClaudeSessionBackup.App\bin\$Configuration\net8.0-windows\ClaudeSessionBackup.exe"

function Pass($m) { Write-Host "  PASS  $m" -ForegroundColor Green }
function Fail($m) { Write-Host "  FAIL  $m" -ForegroundColor Red }
function Info($m) { Write-Host "  INFO  $m" -ForegroundColor DarkGray }

if (-not (Test-Path $exe)) {
    Info "App exe not found at $exe - SKIPPED (build the solution first)"
    exit 0
}

# Refuse to run beside the user's own instance: --demo would be a second window
# and Stop-Process at the end could take theirs down with it.
if (@(Get-Process -Name 'ClaudeSessionBackup' -ErrorAction SilentlyContinue).Count -gt 0) {
    Fail 'ClaudeSessionBackup.exe is ALREADY RUNNING - close it (check the notification area) and re-run'
    exit 1
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -Namespace Tsx -Name N -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
'@

$UIA = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$ANY = [System.Windows.Automation.Condition]::TrueCondition

function Get-Class([IntPtr]$h) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][Tsx.N]::GetClassName($h, $sb, 256)
    return $sb.ToString()
}

# A MessageBox is a separate top-level window of the same process; MainWindowHandle
# keeps reporting the real one. Enumerate instead of asking the Process object.
function Get-DialogCount([int]$targetPid) {
    # Counted through $script: scope, not a local: the callback below runs in its
    # own scope and cannot write a variable in this function's.
    $cb = [Tsx.N+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $wpid = 0
        [void][Tsx.N]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq $targetPid -and [Tsx.N]::IsWindowVisible($h)) {
            if ((Get-Class $h) -eq '#32770') { $script:__dlg++ }
        }
        return $true
    }
    $script:__dlg = 0
    [void][Tsx.N]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:__dlg
}

$proc = $null
$failed = $false
try {
    $proc = Start-Process -FilePath $exe -ArgumentList '--demo' -PassThru
    Start-Sleep -Seconds 6
    $proc.Refresh()

    if ($proc.HasExited) { Fail "demo instance exited with code $($proc.ExitCode)"; exit 1 }
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Fail 'demo instance produced no window'; exit 1 }

    [void][Tsx.N]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 500
    $root = $UIA::FromHandle($hwnd)

    # Nav radios hold a StackPanel as Content, so they have no UIA Name;
    # Capture-Screenshots.ps1 picks them positionally and so does this.
    #
    # Polled, not slept: how long the automation tree takes to populate depends on
    # the machine and on whether the shell has the exe warm, and a fixed sleep that
    # is long enough here is a flaky gate somewhere else.
    # The nav rail is 220 px wide, so "in the rail" is X relative to the WINDOW's
    # left edge - not an absolute screen X. Capture-Screenshots.ps1 can compare
    # against a bare 220 because it first moves the window to a known rectangle;
    # this script leaves the window where it opens, which on a centred 1180 px
    # window is several hundred px in, and an absolute test found nothing.
    $radios = @()
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $root = $UIA::FromHandle($proc.MainWindowHandle)
        $left = $root.Current.BoundingRectangle.X
        $radios = @($root.FindAll($TS::Descendants, $ANY) | Where-Object {
                $_.Current.ControlType.ProgrammaticName -eq 'ControlType.RadioButton' -and
                ($_.Current.BoundingRectangle.X - $left) -lt 240
            } | Sort-Object { $_.Current.BoundingRectangle.Y })
        if ($radios.Count -ge 6) { break }
        Start-Sleep -Milliseconds 700
        $proc.Refresh()
    }
    if ($radios.Count -lt 6) { Fail "expected 6 nav items in the rail, found $($radios.Count)"; exit 1 }

    $radios[5].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 3

    $list = @($root.FindAll($TS::Descendants, $ANY) | Where-Object {
        $_.Current.ControlType.ProgrammaticName -eq 'ControlType.List' -and
        $_.Current.BoundingRectangle.Width -gt 500
    })[0]
    if (-not $list) { Fail 'turn list not found on the Transcript page'; exit 1 }

    $scroll = $list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $scroll.Current.VerticallyScrollable) {
        Fail 'turn list does not scroll - too few turns for containers to recycle, so this check would prove nothing'
        exit 1
    }

    $none = [System.Windows.Automation.ScrollAmount]::NoAmount
    $seen = New-Object System.Collections.Generic.HashSet[int]

    foreach ($c in 1..$Cycles) {
        foreach ($dir in @([System.Windows.Automation.ScrollAmount]::LargeIncrement,
                           [System.Windows.Automation.ScrollAmount]::LargeDecrement)) {
            for ($i = 0; $i -lt 12; $i++) {
                # Throws at either end of the range; that is the end, not a fault.
                try {
                    $scroll.Scroll($none, $dir)
                    [void]$seen.Add([int]$scroll.Current.VerticalScrollPercent)
                } catch { }
                Start-Sleep -Milliseconds 80
            }
        }

        if ($proc.HasExited) { Fail "app died during scroll cycle $c"; $failed = $true; break }
        if ((Get-DialogCount $proc.Id) -gt 0) {
            Fail "app raised an error dialog during scroll cycle $c"
            Info 'That is the FlowDocument ownership fault - see Views\MarkdownHost.cs.'
            Info "Details: $(Join-Path $env:APPDATA 'ClaudeSessionBackup\error.log')"
            $failed = $true
            break
        }
    }

    if (-not $failed) {
        if ($seen.Count -lt 3) {
            Fail "the list barely moved ($($seen.Count) distinct positions) - this run proved nothing"
            $failed = $true
        } else {
            Pass "Transcript survived $($Cycles * 24) page scrolls over $($seen.Count) positions, no dialog"
        }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}

exit ([int]$failed)
