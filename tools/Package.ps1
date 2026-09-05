<#
    Package.ps1 - build a folder a RECIPIENT can run.

    Install.cmd in the repo builds from source, which needs a clone and the .NET
    SDK. Nobody should have to install a compiler to receive a backup tool, so
    this produces a self-contained package instead: a zip they extract and run.

    TWO SHAPES.

    Self-contained (default)
        The .NET runtime is packed in. The recipient needs nothing else installed.
        About 110 MB on disk, ~65 MB zipped.

    Framework-dependent  (-FrameworkDependent)
        About 5 MB zipped, but only starts if the machine already has .NET Desktop
        Runtime 8.0 or newer. Pick this when you know the fleet, or when the zip
        has to fit in a size-limited channel.

    THE DEFAULT IS THE BIG ONE ON PURPOSE. A 65 MB download is a one-off nuisance;
    "it says .NET is missing and I don't have admin" is a support conversation, and
    the person hitting it is someone trying to back up their Claude sessions, not a
    developer.

    The package contains the SAME Install.cmd the repo uses. It notices the app\
    payload sitting beside it and copies that instead of building, so there is
    one installer to reason about rather than two.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\tools\Package.ps1
        powershell -ExecutionPolicy Bypass -File .\tools\Package.ps1 -FrameworkDependent
        powershell -ExecutionPolicy Bypass -File .\tools\Package.ps1 -Runtime win-arm64
        powershell -ExecutionPolicy Bypass -File .\tools\Package.ps1 -NoZip
        powershell -ExecutionPolicy Bypass -File .\tools\Package.ps1 -OutDir C:\temp\pkg
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [switch] $FrameworkDependent,
    [switch] $NoZip,
    # Where the staged folder and the zip go. Default <repo>\dist. Verify.ps1's
    # packaging gate passes a temp folder so a gate run can never overwrite or
    # delete the release artefacts sitting in dist\.
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Step { param([string] $T) Write-Host "==> $T" -ForegroundColor Cyan }
function Ok   { param([string] $T) Write-Host "    $T" -ForegroundColor Green }
function Note { param([string] $T) Write-Host "    $T" -ForegroundColor DarkGray }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found on PATH. Packaging needs it; installing the package does not."
}

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $version) { $version = '1.0.0' }

$kind   = if ($FrameworkDependent) { 'runtime-required' } else { 'self-contained' }
$distRoot = if ($OutDir) { $OutDir } else { Join-Path $repo 'dist' }
$stage    = Join-Path $distRoot "Claude Session Backup $version"
$appDir = Join-Path $stage 'app'

Write-Host ''
Write-Host "Claude Session Backup - package $version ($kind, $Runtime)" -ForegroundColor Cyan
Write-Host '--------------------------------------------------------------' -ForegroundColor Cyan

# ---------------------------------------------------------------- clean
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

# ---------------------------------------------------------------- publish
Step "Publishing ($Configuration)"

# Both projects publish into ONE folder: the CLI must resolve beside the app exe
# (the Schedule page looks for it there), so a split layout breaks the schedule.
$common  = @('-c', $Configuration, '-r', $Runtime, '-o', $appDir, '--nologo')
$common += if ($FrameworkDependent) { '--self-contained:false' } else { '--self-contained:true' }

foreach ($proj in @('src\ClaudeSessionBackup.App\ClaudeSessionBackup.App.csproj',
                    'src\ClaudeSessionBackup.Cli\ClaudeSessionBackup.Cli.csproj')) {
    & dotnet publish (Join-Path $repo $proj) @common | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $proj (exit $LASTEXITCODE)." }
}

foreach ($required in @('ClaudeSessionBackup.exe', 'ClaudeSessionBackup.Cli.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $required))) {
        throw "Expected '$required' in the payload but it is missing."
    }
}
Ok "payload: $appDir"

# Strip PDBs. They are debug symbols, roughly a third of the app's own size,
# and a recipient has no use for them; they do not affect the app's behaviour.
Get-ChildItem $appDir -Filter *.pdb -Recurse | Remove-Item -Force

# ---------------------------------------------------------------- installer files
Step 'Adding the installer and license'

foreach ($f in @('Install.cmd', 'Install.ps1', 'Uninstall.cmd', 'Uninstall.ps1', 'LICENSE', 'README.md')) {
    $src = Join-Path $repo $f
    if (Test-Path $src) { Copy-Item $src $stage -Force; Ok $f }
}

# ---------------------------------------------------------------- zip
$sizeMb = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

if (-not $NoZip) {
    Step 'Compressing'
    $suffix = if ($FrameworkDependent) { "-runtime-required" } else { "" }
    $zip    = Join-Path $distRoot "ClaudeSessionBackup-$version-$Runtime$suffix.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Ok "$zip  ($zipMb MB)"
}

Write-Host ''
Write-Host "Packaged $kind - $sizeMb MB unpacked." -ForegroundColor Green
Write-Host ''
Note 'Send the zip. The recipient extracts it anywhere and double-clicks Install.cmd.'
Note 'Windows marks a downloaded zip as blocked: if Install.cmd refuses to run,'
Note 'right-click the ZIP > Properties > Unblock, THEN extract. Unblocking after'
Note 'extraction does not clear the mark on the files inside.'
