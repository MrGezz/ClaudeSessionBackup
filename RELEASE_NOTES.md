# Claude Session Backup 1.0.0

Released 2026-09-06. First release.

## What it does

Backs up every Claude Desktop (Cowork) and Claude Code conversation store on a Windows machine
into a folder neither app touches, keeps a session catalog, and can rebuild a wiped Cowork sidebar
from that catalog. Built because on 2026-08-22 the desktop app silently wiped the sidebar index,
and Claude Code's mtime-keyed auto-cleanup (`cleanupPeriodDays`, default 30) will start deleting
transcripts unless set high enough - and a high value is not a guarantee.

## Stores

Thirteen source stores are discovered and backed up per run:

- **code-transcripts** - `*.jsonl` transcripts and per-project auto-memory under `~\.claude\projects`.
- **code-config** - whitelisted settings, history, keybindings, tasks, scheduled tasks, skills,
  commands, plans, todos, agents, hooks. Never `.credentials.json` or `.claude.json`.
- **cowork-index** - sidebar records and `deleted_<cliSessionId>` markers.
- **cowork-agent-mode** - VM / agent-mode sessions (excluding `rpm\`).
- **cowork-scratch** - working files of "No folder" sessions.
- **cowork-config** - desktop app config files and logs.
- **cowork-logs** - the desktop app's live logs (`%LOCALAPPDATA%\Claude\Logs`).
- **cowork3p-index** / **cowork3p-agent-mode** - a second (dormant) Claude Desktop profile.
- **msix-index** / **msix-agent-mode** / **msix-scratch** / **msix-config** - Optional stores
  under the MSIX package container, present when the Store build virtualises the filesystem.

## Safety

1. Never deletes or truncates at the destination. An empty source next to a non-empty backup
   is reported as a wipe, not mirrored.
2. Shrink guard: a transcript smaller at the source than in the backup is held back and quarantined.
3. Copies go to a `.partial` name and are moved into place; a crash never leaves a half file that
   looks valid.
4. Snapshot retention moves old zips to `_to_delete\`, never deletes them.
5. Secrets are never copied.
6. The sidebar rebuild refuses to write while the desktop app runs, backs the index folder up first,
   and never resurrects app-deleted sessions unless asked.
7. One run at a time per destination (`.lock`, stale after 3 h).
8. Store discovery: every run scans for Claude data roots outside any covered store and warns.

## The app

Six-page WPF desktop shell with CyanogenMod-2 dark/light theme:

- **Dashboard** - store status grid, Backup / Verify / Cancel, run log, scheduled-task status.
- **Catalog** - every session with title, project, prompts/replies, size, live/backup status.
- **Restore** - LOST and DANGLING lists, restore guide, sidebar rebuild (plan then apply).
- **Transcript** - reads backup or live transcripts: thinking, tool calls, results, system notes,
  attachments, images decoded inline; text search, copy Markdown, export to `.md` / `.html`.
- **Schedule** - daily time, at-logon trigger, install/uninstall the Windows scheduled task.
- **Settings** - destination, include subagents, snapshot retention, theme.

Minimise and close hide to the notification area (tray icon with Open, Backup now, Verify, Exit).
Single-instance; a startup fault writes `startup-error.log` and exits rather than holding the mutex.

## CLI

```
ClaudeSessionBackup.Cli backup | verify | catalog | rebuild | export | install-task | uninstall-task | task-status
```

Exit codes: 0 ok, 1 ok with warnings, 2 failures, 3 usage/config error, 4 refused (lock or desktop
app running). The scheduled task runs `backup --quiet` daily and 3 minutes after logon.

## Demo mode

`ClaudeSessionBackup.exe --demo` runs the app with fictional stores, catalog, and transcript data
so screenshots and demos never expose real session content. Screenshots are produced only through
`tools\Capture-Screenshots.ps1 -Demo`; the privacy gate (`tools\Check-Privacy.ps1`) enforces this.

## Machine tag and shared-destination warning

When two machines share one backup destination, each run is tagged with the machine name and a
warning is shown when the previous run came from a different machine.

## Install

Download `ClaudeSessionBackup-1.0.0-Setup.exe` (recommended) or `ClaudeSessionBackup-1.0.0-win-x64.zip`
from the Releases page. The installer is per-user by default (no admin needed); all-users is offered
in the wizard.

The Setup.exe is unsigned: SmartScreen shows "Windows protected your PC" on a machine that has not
seen it. More info, then Run anyway.

## Verified by

Build 0 errors (nullable + async warnings are errors), 176 tests (temp trees only),
`tools\Verify.ps1` 7 gates PASS (build, tests, CLI smoke, theme keys, launch, packaging, privacy).
