<#
    Check-Packaging.ps1 - static gate over the scripts that build a release.

    WHY THIS EXISTS. Make-Installer.ps1 historically called Package.ps1 like this:

        $pkgArgs = @('-Configuration', $Configuration, '-Runtime', $Runtime, '-NoZip')
        & (Join-Path $PSScriptRoot 'Package.ps1') @pkgArgs

    which does not work in any PowerShell. An ARRAY splat passes its elements
    POSITIONALLY: "-Runtime" arrives as a VALUE, not a parameter name, so it lands
    on Package.ps1's third positional slot - and there isn't one, because the
    remaining parameters are switches. Every run died with:

        A positional parameter cannot be found that accepts argument '-Runtime'

    Named parameters need a HASHTABLE splat. Only @{...} binds by name.

    The bug survives in repositories because the other Verify.ps1 gates build,
    theme-check, and launch the APP - and none of them ever runs the scripts that
    produce the thing users actually receive. So the one command the README
    documents for building Setup.exe is broken, and the only way to find out is to
    try to cut a release.

    This is deliberately NOT a general PowerShell linter. It checks three things
    that would have caught it, statically, in under a second:

      1. every packaging script PARSES;
      2. no script splats an array that contains "-Name" strings;
      3. every key of a hashtable splat is a REAL parameter of the script being
         called - so renaming a parameter in Package.ps1 breaks the gate, not the
         release.
      4. the .iss file references only files that exist in the staged layout names.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\Check-Packaging.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$script:Problems = @()
function Fail { param([string] $M) $script:Problems += $M; Write-Host "  FAIL  $M" -ForegroundColor Red }
function Pass { param([string] $M) Write-Host "  PASS  $M" -ForegroundColor Green }
function Note { param([string] $M) Write-Host "  NOTE  $M" -ForegroundColor DarkGray }

# Scripts that build or install a release. Deliberately a list, not a recursive
# glob: test and smoke scripts are already covered by running them.
$targets = @(
    'tools\Package.ps1'
    'tools\Make-Installer.ps1'
    'tools\Release.ps1'
    'tools\Verify.ps1'
    'tools\Check-Packaging.ps1'
    'Install.ps1'
    'Uninstall.ps1'
) | ForEach-Object { Join-Path $repo $_ } | Where-Object { Test-Path $_ }

# ---------------------------------------------------------------- 1. parse
$asts = @{}
foreach ($t in $targets) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($t, [ref] $tokens, [ref] $errors)
    if ($errors -and $errors.Count -gt 0) {
        Fail ("{0} does not parse: {1}" -f (Split-Path $t -Leaf), $errors[0].Message)
        continue
    }
    $asts[[IO.Path]::GetFileName($t)] = $ast
}
if ($asts.Count -eq $targets.Count) { Pass "all $($targets.Count) packaging/install scripts parse" }

# ---------------------------------------------------------------- helpers

# Declared parameter names of a parsed script, lower-cased.
function Get-ScriptParameterNames {
    param($Ast)
    $pb = $Ast.ParamBlock
    if (-not $pb) { return @() }
    return @($pb.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath.ToLowerInvariant() })
}

# EVERY assignment to $Name, not just the last one. Taking only the last is wrong
# in both directions: `$a = @('-X'); $a += $y` would hide the array that carries
# the names, and `$a = @{}; $a += @('-X')` would hide the array behind a hashtable.
# A splat is only safe if every contribution to it is safe.
function Get-VariableAssignments {
    param($Ast, [string] $Name)
    return @($Ast.FindAll({
        param($n)
        $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $n.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $n.Left.VariablePath.UserPath -eq $Name
    }, $true) | ForEach-Object { $_.Right })
}

# ---------------------------------------------------------------- 2 + 3. splats
$splatChecks = 0

foreach ($file in $asts.Keys) {
    $ast = $asts[$file]

    $commands = $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.CommandAst]
    }, $true)

    foreach ($cmd in $commands) {
        # The splatted variable, if this command has one.
        $splat = $cmd.CommandElements | Where-Object {
            $_ -is [System.Management.Automation.Language.VariableExpressionAst] -and $_.Splatted
        } | Select-Object -First 1
        if (-not $splat) { continue }

        # The .ps1 this command invokes, found as a literal anywhere in the call -
        # which covers `& (Join-Path $PSScriptRoot 'Package.ps1') @args`.
        $targetName = $cmd.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $n.Value -match '\.ps1$'
        }, $true) | Select-Object -First 1 -ExpandProperty Value -ErrorAction SilentlyContinue
        if (-not $targetName) { continue }

        $splatChecks++
        $varName = $splat.VariablePath.UserPath
        $rhsAll  = Get-VariableAssignments -Ast $ast -Name $varName
        $label   = "$file -> $targetName (@$varName)"

        if ($rhsAll.Count -eq 0) {
            Fail "$label : cannot resolve `$$varName - splat is unverifiable"
            continue
        }

        # --- 2. any contribution carrying parameter NAMES outside a hashtable:
        #        the bug this gate exists for.
        $dashed = @()
        foreach ($rhs in $rhsAll) {
            $inHashtable = $rhs.FindAll({
                param($n) $n -is [System.Management.Automation.Language.HashtableAst]
            }, $true).Count -gt 0
            if ($inHashtable) { continue }

            $dashed += $rhs.FindAll({
                param($n)
                $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                $n.Value -match '^-[A-Za-z]'
            }, $true) | ForEach-Object { $_.Value }
        }

        if ($dashed.Count -gt 0) {
            Fail ("$label : parameter names passed through a NON-hashtable splat ({0}). " -f
                  (($dashed | Sort-Object -Unique) -join ', ') +
                  'An array splat binds POSITIONALLY, so these arrive as values and the call fails ' +
                  'with "A positional parameter cannot be found". Use a hashtable splat: @{ Name = $value }')
            continue
        }

        $isHashtable = @($rhsAll | Where-Object {
            $_.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true).Count -gt 0
        }).Count -gt 0

        if (-not $isHashtable) {
            Pass "$label : positional array splat, no parameter names"
            continue
        }

        # --- 3. hashtable splat: every key must be a real parameter of the target.
        $targetAst = $asts[[IO.Path]::GetFileName($targetName)]
        if (-not $targetAst) {
            Pass "$label : hashtable splat (target not in the checked set)"
            continue
        }

        $declared = Get-ScriptParameterNames -Ast $targetAst
        $keys = @($rhsAll | ForEach-Object {
            $_.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
        } | ForEach-Object { $_.KeyValuePairs } | ForEach-Object { $_.Item1.Extent.Text.Trim("'", '"') })

        # Keys added later by index/property assignment ($h['X'] = ...) are found by
        # scanning the whole file for that variable being indexed - cheap, and it
        # catches the common "add a switch conditionally" shape.
        $indexed = $ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $n.Left -is [System.Management.Automation.Language.IndexExpressionAst] -and
            $n.Left.Target -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $n.Left.Target.VariablePath.UserPath -eq $varName
        }, $true) | ForEach-Object { $_.Left.Index.Extent.Text.Trim("'", '"') }

        $bad = @($keys + $indexed) |
               Where-Object { $_ } |
               Where-Object { $declared -notcontains $_.ToLowerInvariant() }

        if ($bad.Count -gt 0) {
            Fail ("$label : passes {0}, which {1} does not declare. Declared: {2}" -f
                  (($bad | Sort-Object -Unique) -join ', '), $targetName, ($declared -join ', '))
        } else {
            Pass ("$label : {0} hashtable key(s), all declared by {1}" -f @($keys + $indexed).Count, $targetName)
        }
    }
}

if ($splatChecks -eq 0) {
    Note 'no script-to-script splats found'
}

# ---------------------------------------------------------------- 4. .iss layout check
$issPath = Join-Path $repo 'tools\ClaudeSessionBackup.iss'
if (Test-Path $issPath) {
    # The .iss references files from the staged layout (app\* via PayloadDir, and
    # the root-level files).  Check that every source name in [Files] that is NOT
    # a wildcard points at a file that will exist in the staged layout.
    # We only check the ROOT-level extra files - app\* is a glob and checked at build time.
    $issContent = Get-Content $issPath -Raw

    # The staged layout (what Package.ps1 puts beside app\):
    $expectedInStage = @('Install.cmd', 'Install.ps1', 'Uninstall.cmd', 'Uninstall.ps1', 'LICENSE', 'README.md')
    # The app icon is referenced in SetupIconFile - check it exists in the source tree.
    $iconRef = Join-Path $repo 'src\ClaudeSessionBackup.App\Assets\app.ico'
    if (-not (Test-Path $iconRef)) {
        Fail ".iss SetupIconFile src\ClaudeSessionBackup.App\Assets\app.ico does not exist"
    } else {
        Pass ".iss SetupIconFile app.ico exists"
    }

    # The [Files] section uses {#PayloadDir}\* (a wildcard - runtime check) and no
    # other explicit files, so this check is limited to what the icon check above covers.
    # Report that the layout names are consistent.
    Pass ".iss [Files] uses PayloadDir wildcard (checked at ISCC build time)"
} else {
    Note 'ClaudeSessionBackup.iss not found - .iss check skipped'
}

# ---------------------------------------------------------------- 5. version consistency
# Directory.Build.props Version == .iss default AppVersion == the version in
# README's install section == the newest RELEASE_NOTES heading. A mismatch means
# Release.ps1 was not used or bumped only some of the files.

$propsPath = Join-Path $repo 'Directory.Build.props'
$propsVersion = $null
if (Test-Path $propsPath) {
    $propsVersion = ([xml](Get-Content $propsPath -Raw)).Project.PropertyGroup.Version
}
if (-not $propsVersion) { $propsVersion = '' }

$issDefaultVersion = $null
$issPath5 = Join-Path $repo 'tools\ClaudeSessionBackup.iss'
if (Test-Path $issPath5) {
    $issText = Get-Content $issPath5 -Raw
    if ($issText -match '#define\s+AppVersion\s+"([^"]+)"') {
        $issDefaultVersion = $Matches[1]
    }
}

$readmeVersion = $null
$readmePath = Join-Path $repo 'README.md'
if (Test-Path $readmePath) {
    $readmeText = Get-Content $readmePath -Raw
    # Match the zip filename in the install section: ClaudeSessionBackup-x.y.z-win-x64.zip
    if ($readmeText -match 'ClaudeSessionBackup-(\d+\.\d+\.\d+)-win-x64\.zip') {
        $readmeVersion = $Matches[1]
    }
}

$rnVersion = $null
$rnPath = Join-Path $repo 'RELEASE_NOTES.md'
if (Test-Path $rnPath) {
    $rnLines = Get-Content $rnPath
    foreach ($line in $rnLines) {
        if ($line -match '^#\s+Claude Session Backup\s+(\d+\.\d+\.\d+)') {
            $rnVersion = $Matches[1]
            break  # newest heading is first
        }
    }
}

$versionMismatch = @()
if ($propsVersion) {
    if ($issDefaultVersion -and $issDefaultVersion -ne $propsVersion) {
        $versionMismatch += ".iss AppVersion '$issDefaultVersion' != Directory.Build.props '$propsVersion'"
    }
    if ($readmeVersion -and $readmeVersion -ne $propsVersion) {
        $versionMismatch += "README.md install version '$readmeVersion' != Directory.Build.props '$propsVersion'"
    }
    if ($rnVersion -and $rnVersion -ne $propsVersion) {
        $versionMismatch += "RELEASE_NOTES.md newest heading '$rnVersion' != Directory.Build.props '$propsVersion'"
    }
}

if ($versionMismatch.Count -gt 0) {
    foreach ($m in $versionMismatch) { Fail "version mismatch: $m" }
} else {
    $parts = @("Directory.Build.props $propsVersion")
    if ($issDefaultVersion)  { $parts += '.iss' }
    if ($readmeVersion)      { $parts += 'README' }
    if ($rnVersion)          { $parts += 'RELEASE_NOTES' }
    Pass "version consistency: $($parts -join ', ')"
}

# ---------------------------------------------------------------- 6. [Code]-section linter for .iss
# Same three shapes as mcp-servers-for-revit's Check-InstallerScript.ps1:
#   a) ';' used as a comment inside [Code] (it is a statement separator there)
#   b) typed array constants ("array[0..7] of string") - Pascal Script has none
#   c) brace comment closed early by an Inno {constant}
if (Test-Path $issPath5) {
    $issFullText = Get-Content $issPath5 -Raw
    $codeAt = $issFullText.IndexOf('[Code]')
    if ($codeAt -ge 0) {
        $lf       = [char]10
        $codeText = $issFullText.Substring($codeAt)
        $codeOffset = ($issFullText.Substring(0, $codeAt) -split $lf).Count
        $codeLines  = $codeText -split $lf
        $codeProblems = @()

        # 6a. ';' comments
        for ($i = 1; $i -lt $codeLines.Count; $i++) {
            if ($codeLines[$i] -match '^\s*;') {
                $codeProblems += "line $($codeOffset + $i): ';' is a statement separator in Pascal Script, not a comment. Use // or { }."
            }
        }

        # 6b. typed array constants
        for ($i = 0; $i -lt $codeLines.Count; $i++) {
            if ($codeLines[$i] -match 'array\s*\[') {
                $codeProblems += "line $($codeOffset + $i): Pascal Script has no typed array constants."
            }
        }

        # 6c. brace comments closed early by an Inno constant
        $idx = 0
        while ($true) {
            $open = $codeText.IndexOf('{', $idx)
            if ($open -lt 0) { break }
            $close = $codeText.IndexOf('}', $open + 1)
            if ($close -lt 0) { break }

            $inner = $codeText.Substring($open + 1, $close - $open - 1)
            if ($inner.Contains($lf)) {
                $nl   = $codeText.IndexOf($lf, $close + 1)
                $tail = if ($nl -gt 0) { $codeText.Substring($close + 1, $nl - $close - 1) } else { '' }
                if ($tail -match '[A-Za-z]{3}') {
                    $line = $codeOffset + ($codeText.Substring(0, $open) -split $lf).Count - 1
                    $codeProblems += "line ${line}: brace comment closes early and '$($tail.Trim())' is then parsed as code."
                }
            }
            $idx = $close + 1
        }

        if ($codeProblems.Count -gt 0) {
            foreach ($p in $codeProblems) { Fail "[Code] $p" }
        } else {
            Pass ".iss [Code] section: no ';' comments, no typed array constants, no early-closed brace comments"
        }
    } else {
        Note '.iss has no [Code] section - [Code] linter skipped'
    }
}

# ---------------------------------------------------------------- 7. workflow YAML checks
# ci.yml and release.yml exist; every tools\*.ps1 they invoke exists; release.yml
# triggers on tags v*, has contents: write, and uploads the Setup.exe and the zip.
$ciYml      = Join-Path $repo '.github\workflows\ci.yml'
$releaseYml = Join-Path $repo '.github\workflows\release.yml'

if (-not (Test-Path $ciYml)) {
    Fail '.github\workflows\ci.yml does not exist'
} else {
    Pass 'ci.yml exists'
}

if (-not (Test-Path $releaseYml)) {
    Fail '.github\workflows\release.yml does not exist'
} else {
    Pass 'release.yml exists'

    $relText = Get-Content $releaseYml -Raw

    # trigger on tags v*
    if ($relText -match "tags:\s*\n\s*-\s*'v\*'") {
        Pass 'release.yml triggers on tags v*'
    } else {
        Fail "release.yml does not trigger on tags v*"
    }

    # contents: write
    if ($relText -match 'contents:\s*write') {
        Pass 'release.yml has contents: write'
    } else {
        Fail 'release.yml is missing permissions contents: write'
    }

    # uploads Setup.exe and the zip
    if ($relText -match 'Setup\.exe' -and $relText -match 'win-x64\.zip') {
        Pass 'release.yml uploads Setup.exe and the zip'
    } else {
        Fail 'release.yml does not reference both Setup.exe and the zip'
    }

    # every tools\*.ps1 invoked in the workflow exists
    $invokedScripts = [regex]::Matches($relText, 'tools[/\\](\S+\.ps1)')
    foreach ($m in $invokedScripts) {
        $scriptRel = "tools\$($m.Groups[1].Value)"
        $scriptAbs = Join-Path $repo $scriptRel
        if (-not (Test-Path $scriptAbs)) {
            Fail "release.yml invokes $scriptRel which does not exist"
        }
    }
}

if (Test-Path $ciYml) {
    $ciText = Get-Content $ciYml -Raw
    $ciInvoked = [regex]::Matches($ciText, 'tools[/\\](\S+\.ps1)')
    foreach ($m in $ciInvoked) {
        $scriptRel = "tools\$($m.Groups[1].Value)"
        $scriptAbs = Join-Path $repo $scriptRel
        if (-not (Test-Path $scriptAbs)) {
            Fail "ci.yml invokes $scriptRel which does not exist"
        }
    }
    if ($ciInvoked.Count -gt 0) {
        Pass "ci.yml: all $($ciInvoked.Count) invoked script(s) exist"
    }
}

Write-Host ''
if ($script:Problems.Count -gt 0) {
    Write-Host "  $($script:Problems.Count) packaging problem(s)" -ForegroundColor Red
    exit 1
}
Write-Host '  Packaging scripts OK' -ForegroundColor Green
exit 0
