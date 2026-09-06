; ============================================================================
;  ClaudeSessionBackup.iss - Inno Setup script for Claude Session Backup.
;
;  Built by tools/Make-Installer.ps1, which publishes the app first and
;  passes the payload path and version in as defines. Don't run ISCC on this
;  directly unless you have already produced dist\Claude Session Backup <ver>\app.
;
;  WHY INNO DOES THE INSTALLING HERE, not Install.ps1. Inno already writes the
;  Add/Remove Programs entry, generates a real uninstaller, handles upgrade over
;  an existing version in place, and refuses to install over a running app.
;  Wrapping our script in it would mean two uninstallers disagreeing about who
;  owns the files. Install.cmd still exists for the zip path and detects a
;  published app\ folder beside it.
;
;  Never call MsgBox from InitializeSetup. /SUPPRESSMSGBOXES does NOT suppress
;  it, so /VERYSILENT blocks forever on a dialog nobody can see - which would
;  break exactly the unattended deployment an installer exists to enable.
;  (Lesson from Sync-ACC, 2026-09-03; cleanup now runs at ssPostInstall.)
; ============================================================================

#ifndef Publisher
  #define Publisher "IcZ Workspace"
#endif

#ifndef AppVersion
  #define AppVersion "1.0.3"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\dist\Claude Session Backup 1.0.3\app"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
; Fixed GUID. Changing it makes Windows treat the next build as a DIFFERENT
; product, so upgrades stop replacing the old one and users collect duplicate
; entries in Apps & features.
AppId={{B2C74A1F-3D85-4F9E-A531-76E8C0D92B47}
AppName=Claude Session Backup
AppVersion={#AppVersion}
AppVerName=Claude Session Backup {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/MrGezz/ClaudeSessionBackup

; Inno stamps the compiled Setup.exe's OWN Win32 version resource from these
; VersionInfo* directives - it does NOT inherit them from AppVersion. Without
; them, Setup.exe shows "0.0.0.0" in Explorer properties, an unversioned installer
; next to the app it installs which does carry 1.0.0.0. Keep this block driven
; by the same two defines so the installer cannot disagree with its payload.
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName=Claude Session Backup
VersionInfoDescription=Claude Session Backup Setup
VersionInfoCompany={#Publisher}
VersionInfoCopyright=Copyright (C) 2026 {#Publisher}

; Per-user install by default. The app must run unelevated (the user's Claude
; stores live in their own profile and the backup destination is usually their
; own folder); all-users shares the binaries but every user still configures
; their own backup destination and scheduled task.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={localappdata}\Programs\ClaudeSessionBackup
DefaultGroupName=Claude Session Backup
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=ClaudeSessionBackup-{#AppVersion}-Setup
SetupIconFile=..\src\ClaudeSessionBackup.App\Assets\app.ico
UninstallDisplayIcon={app}\ClaudeSessionBackup.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; The app's single-instance mutex. Inno checks this and asks the user to close
; Claude Session Backup before writing over files that are currently loaded.
AppMutex=ClaudeSessionBackup.App.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut off by default - only add to the desktop when the user
; actively wants it there.
Name: "desktopicon";  Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
; The daily backup task is opt-in. Running it for someone who did not ask means
; a surprise process in their task scheduler and a backup they did not want yet.
Name: "registertask"; Description: "Register the &daily backup task (runs ClaudeSessionBackup.Cli.exe install-task)"; GroupDescription: "After install:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Claude Session Backup"; Filename: "{app}\ClaudeSessionBackup.exe"
Name: "{autodesktop}\Claude Session Backup"; Filename: "{app}\ClaudeSessionBackup.exe"; Tasks: desktopicon

[Run]
; Register the task first, before launching the app, so the Schedule page already
; shows it as registered on first launch when the user chose that task.
Filename: "{app}\ClaudeSessionBackup.Cli.exe"; Parameters: "install-task"; Tasks: registertask; Flags: runhidden skipifdoesntexist; StatusMsg: "Registering backup task..."
; Launch the app after install so the user can configure their backup destination
; without having to find it in the Start menu.
Filename: "{app}\ClaudeSessionBackup.exe"; Description: "Launch Claude Session Backup"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; BEFORE the files go: unregister the task before deleting the CLI it points at.
; Task Scheduler keeps firing at a missing binary every few hours and failing
; quietly into a log nobody reads. Exits 0 when no task is registered.
Filename: "{app}\ClaudeSessionBackup.Cli.exe"; Parameters: "uninstall-task"; RunOnceId: "UnregisterBackupTask"; Flags: runhidden skipifdoesntexist

[Code]
{ ------------------------------------------------------------------------
  Post-uninstall message. Tells the user what was deliberately NOT removed,
  because people reasonably fear that uninstalling a backup tool removes what
  it backed up. Skipped in silent mode so unattended removal stays unattended.
  ------------------------------------------------------------------------ }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    MsgBox('Claude Session Backup has been removed.' + #13#10#13#10 +
           'Kept on purpose:' + #13#10 +
           '  - every file already backed up to your backup destination' + #13#10 +
           '  - your settings (backup destination, options) in %APPDATA%\ClaudeSessionBackup' + #13#10#13#10 +
           'Delete those by hand if you actually want them gone.',
           mbInformation, MB_OK);
end;
