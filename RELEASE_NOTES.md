# Claude Session Backup 1.0.5

Released 2026-10-02. Upgrade in place with `ClaudeSessionBackup-1.0.5-Setup.exe`; close the app first,
the installer waits on its mutex.

## Added

- **ZCode is backed up too.** Five new stores cover the ZCode agent's data under `~\.zcode\cli`:
  `zcode-transcripts` (the `model-io-sess_<uuid>.jsonl` conversation dumps, append-only, shrink-guarded
  like Claude transcripts and excluded from snapshots for size), `zcode-db` (the session index sqlite,
  snapshotted; the `-shm` shared-memory file is excluded), `zcode-artifacts` (per-session tool results
  and media), `zcode-memories` (persistent per-project memory) and `zcode-config` (`config.json` only -
  registered MCP servers, hooks, plugins). All five are **Optional**: if ZCode is uninstalled or moves
  its data root, their absence is an INFO line, not five warnings. The Dashboard now shows 18 stores.
- **`credentials.json` joined the global secret list.** `~\.zcode\v2` holds ZCode's sign-in secret;
  no store roots there (the same ruling as the Claude-3p profile root), and the file name is excluded
  by every copier as defence in depth.

## Deliberately not a store

- `~\.zcode\cli\exec` (ephemeral shell snapshots, regenerated per session), `cli\log` (app telemetry),
  `cli\plugins` (a package cache, like `rpm\` and `node_modules`) and all of `~\.zcode\v2` (secrets).
  The ZCode transcript viewer is not wired up either - the rollout format is ZCode's own and is stored
  verbatim; the Transcript page still reads Claude's `*.jsonl` format.

# Claude Session Backup 1.0.4

Released 2026-09-27. Upgrade in place with `ClaudeSessionBackup-1.0.4-Setup.exe`; close the app first,
the installer waits on its mutex.

## Changed

- **The look.** The app now carries the visual grammar of the IcZ showcase site, referenced from the IcZ
  Patcher's theme layer rather than copied: a cyan-to-violet rule under the title bar and on the selected
  nav item, a faint graph-paper ground behind every page, card borders that take the accent under the
  pointer, bold uppercase kickers on card labels, 26 px headline figures on the Dashboard metrics and the
  Restore counts, and a small brand tile in the status strip. Same solid CM2 palette, no backdrop, both
  themes.
- **The two `cowork3p-*` stores are Optional.** That dormant second profile lost its two session folders
  between 2026-09-15 and 2026-09-20 while its root stayed, and every run since had warned "source
  missing" twice for stores whose backup copy was intact. An absent optional store is INFO with a muted
  "absent" pill; if the folders come back they are copied as before. The two warnings that profile raised
  on every run are gone.

## Added

- **A nudge on the Dashboard when no daily task is registered**, beside the last-run summary, with a
  button to the Schedule page. Registering is still your choice; the nudge exists because a task can go
  quietly (this machine's did, between 2026-09-08 and 2026-09-27) and until now the only sign was a grey
  "No scheduled task" line.
- **The Catalog grid sits in a card** like the Dashboard's table, so the page ground never shows through
  its rows.

## Verify

- The theme-key gate now also checks the site-look keys (`AccentGradient*`, `AccentBrand*`, `GridLine*`,
  `GraphPaper*`) for parity between the two themes, not only `AppAccent*`, `Status*`, `Panel*` and `Log*`.

# Claude Session Backup 1.0.3

Released 2026-09-07. Upgrade in place with `ClaudeSessionBackup-1.0.3-Setup.exe`; close the app first,
the installer waits on its mutex.

## Fixed

- **The Transcript page crashed when you scrolled.** Opening a transcript and scrolling a text turn out
  of view and back raised "Claude Session Backup hit an unexpected error - Document belongs to another
  FlowDocumentScrollViewer already". A rendered Markdown document belongs to exactly one viewer, and the
  turn list recycles its rows as they leave the screen, so the row that was put away kept hold of the
  document and the row that came back could not have it. Each row now renders its own copy and releases
  it on the way out. Long transcripts also use less memory, because only what is on screen is held.
- **A crash after startup left nothing to report.** The error box was all there was; its details went
  when you clicked OK. Faults are now appended to `%APPDATA%\ClaudeSessionBackup\error.log` (kept under
  256 KB) before the box appears, so a crash can be diagnosed after the fact.
- **Settings** was the only page whose cards stopped short of the window edge.

## Added

- **Four figures across the top of the Dashboard**: stores backed up, files in backup, backup size, and
  **held back** - the files the shrink guard refused to overwrite plus any it could not copy. That last
  number is 0 on a healthy run and only ever appeared in the log before.
- **Proper empty states.** The Catalog now says whether it has never been built (with a button to build
  it) or whether your search simply matched nothing; Restore and Transcript explain what to do next
  instead of showing a bare sentence or an empty grid.
- **The running version** is shown in the status strip, and a card in the sidebar states the rule the
  tool is built on: nothing at the destination is ever deleted.

## Changed

- `tools\Check-TranscriptScroll.ps1` runs inside Verify's launch gate and drives the Transcript page in
  demo mode, so the crash above cannot come back unnoticed. Opening the window was previously the whole
  of that gate.
- The launch gate's startup-error check watched a path the app never writes (`...\logs\startup-error.log`
  rather than `...\startup-error.log`), so it could not have failed however badly startup broke.
- `.gitattributes` now pins line endings: LF in the working tree, CRLF for `.cmd`, `.bat` and `.iss`
  (cmd.exe mis-parses LF-only labels, and both scripts ship in the zip), binary for images and archives.

# Claude Session Backup 1.0.2

Released 2026-09-06. Upgrade in place with `ClaudeSessionBackup-1.0.2-Setup.exe`; close the app first,
the installer waits on its mutex.

## Fixed
- **A junction inside a store no longer stops the backup.** The Claude harness creates directory
  junctions inside a session's `subagents\workflows` folder; Windows refused to traverse one
  ("untrusted mount point") and the whole run aborted before anything was written. The walk now
  never follows junctions or symlinks (a target inside the store is copied under its real path, so
  nothing is lost or duplicated), skips any folder it cannot read with a logged reason, and one
  failing store can no longer abort the other twelve or the run's manifest.
# Claude Session Backup 1.0.1

Released 2026-09-06. Upgrade in place with `ClaudeSessionBackup-1.0.1-Setup.exe`; close the app first,
the installer waits on its mutex. After upgrading, open Schedule and click **Register task** once so the
daily task points at the installed CLI rather than a build folder.

## Fixed

- **The daily task could fail silently.** A task registered from a development build pointed at a CLI
  exe that did not exist. Task Scheduler showed "Ready" and failed every trigger with 0x80070002. The app
  now refuses to register a task whose exe does not exist, finds the CLI beside the app or in the CLI
  project's build output, decodes the last result (the CLI's exit codes and the common Windows errors)
  on the Dashboard and the Schedule page, and shows a warning card when the task's exe is missing or its
  last run failed. A task that never ran reads "never run" instead of 1999-11-30.
- **Dashboard at small window heights.** The store table pushed the last-run card, the buttons and the
  log below the window. The table and the log now share the height and scroll inside their cards; the
  Store and Backup columns no longer clip.

## Changed

- **Settings page** uses the same card-and-toggle layout as ACC Sync: Backup, Appearance (dark theme)
  and Window (minimise / close to the notification area) with pill switches. The page scrolls.
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
