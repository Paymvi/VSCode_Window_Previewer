# Project Previews: technical reference

Details for developers and curious users. Everyday use is covered in the [README](../README.md). Design reasoning,
measurements and the full validation log are in [IMPLEMENTATION_NOTES.md](../IMPLEMENTATION_NOTES.md).

- [Requirements and deployment](#requirements-and-deployment)
- [Command-line options](#command-line-options)
- [Files and registry](#files-and-registry)
- [How names are chosen](#how-names-are-chosen)
- [How windows are identified](#how-windows-are-identified)
- [The companion extension](#the-companion-extension)
- [Taskbar integration](#taskbar-integration)
- [Interaction details](#interaction-details)
- [Window previews](#window-previews)
- [Tested configurations](#tested-configurations)
- [Building and testing](#building-and-testing)
- [Diagnostics](#diagnostics)
- [Privacy and network](#privacy-and-network)

## Requirements and deployment

| Item | Detail |
| --- | --- |
| Windows | Windows 11, tested on build 10.0.26200. Windows 10 is declared in the app manifest and handled in code, but **untested**. |
| Architecture | x64 build (`win-x64`). |
| Runtime | **.NET 9 Desktop Runtime** (`Microsoft.WindowsDesktop.App` 9.x). The app is *framework-dependent*: a single ~0.5 MB `ProjectPreviews.exe` that uses the installed runtime. If the runtime is missing, the standard .NET launcher shows a message pointing to the download. That case wasn't tested here because the runtime was already installed. |
| Privileges | Runs as a normal user (`asInvoker`). No administrator rights needed. VS Code running *as administrator* hasn't been tested. |
| VS Code | VS Code stable (`Code.exe`). VS Code Insiders, VSCodium, Cursor and other forks are excluded in v1. |
| Locations | Build output: `publish\ProjectPreviews\ProjectPreviews.exe`, with `companion\project-previews-companion.vsix` next to it. Portable zip: `dist\ProjectPreviews-1.0.0-win-x64.zip`. The app runs from any folder; nothing is installed. |
| Instances | Single instance per user session. Launching it again signals the running copy to open its Settings, and the new process exits. |

## Command-line options

| Option | Effect |
| --- | --- |
| *(none)* | Starts in the tray. Opens Settings if no settings file exists yet, otherwise shows a short "Running…" notification. |
| `--background` | Starts silently in the tray. "Start with Windows" uses this. |
| `--settings-path <file>` | Uses a different settings file. Intended for testing and screenshots. |

## Files and registry

| What | Where | Written by | Notes |
| --- | --- | --- | --- |
| Settings | `%APPDATA%\ProjectPreviews\settings.json` | App, only when you change a setting | JSON, human-readable. Written atomically (temp file, then rename). |
| Damaged-settings backup | `%APPDATA%\ProjectPreviews\settings.corrupt-<yyyyMMdd-HHmmss>.json` | App | If `settings.json` can't be parsed, it is copied here, defaults are used, and Settings shows a notice. If the copy fails, or the file can't be read at all, the app uses defaults **without ever saving over the original**. Out-of-range values are clamped and invalid colors are dropped. |
| Window reports | `%LOCALAPPDATA%\ProjectPreviews\windows\<session>.json` | Companion extension | One per VS Code window, deleted when the window closes. The app deletes leftovers whose VS Code process has ended. |
| Debug log | `%LOCALAPPDATA%\ProjectPreviews\debug.log` | App, only when `PROJECT_PREVIEWS_DEBUG=1` | Hover timing only. |
| Startup entry | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ProjectPreviews` = `"<exe path>" --background` | App, only when **Start with Windows** is turned on | Removed when you turn it off. Settings shows the box as checked only if the entry points to *this* exe, so after moving the app, turn it on again (that overwrites the old entry). |
| Companion extension | VS Code's extensions folder (`%USERPROFILE%\.vscode\extensions\projectpreviews.project-previews-companion-1.0.0`) | VS Code, when you install it | |

Read-only, never modified:

- `%USERPROFILE%\.claude\status-indicator\config.json` (Claude Workers aliases).
- `%APPDATA%\Code\User\globalStorage\storage.json` (VS Code's list of open folders; used only without the companion).

## How names are chosen

Each window's display name (its *alias*) comes from the first of these that applies:

1. **Your alias** from Settings → Projects and aliases. Stored under the project's *normalized full path*: full path,
   backslashes, no trailing separator, lower-case. A saved `.code-workspace` uses the workspace file's path, and a
   remote folder uses its URI. Because it's per path, two different folders both called `Website` can have
   different aliases. A blank alias falls through to the next rule.
2. **Claude Workers' alias**: the `projectAliases` object in `%USERPROFILE%\.claude\status-indicator\config.json`.
   It's matched by **folder (or workspace) name, case-insensitively**, the same way Claude Workers matches it, so
   it applies to every folder with that name. Claude Workers doesn't need to be running. The file is re-read
   whenever it changes; comments and trailing commas are tolerated, and non-text values are ignored.
3. **The folder or workspace name**, with underscores shown as spaces (Claude Workers' rule): `Weather_Dashboard`
   becomes "Weather Dashboard". A saved workspace `demo.code-workspace` (title "demo (Workspace)") becomes "demo".

Special cases:

- A window with no folder open shows **Empty window**.
- If the project can't be identified, the card shows the name taken from the window title and the subtitle reads
  "Project not identified". This happens without the companion when VS Code's title is customized or ambiguous.
  **Your path-based aliases are never applied to such a guess**; Claude Workers' name-based aliases still are.
- Two windows of the *same* project get "1 of 2" / "2 of 2" badges.
- *Different* projects that end up with the same name get their parent folder added to the subtitle, for example
  "index.html · Northwind" and "styles.css · archive".
- Accent color: your choice from Settings, otherwise a color derived from the project path, so it stays the same
  across launches.

## How windows are identified

The app lists visible, top-level `Code.exe` windows on the **current virtual desktop**, in most-recently-used
order. For each window it determines the project:

**With the companion extension.** Each window's extension reports its exact folders, its workspace file, the name
of the active tab, and when the window last gained focus. The extension can't know which taskbar window it
belongs to, so the app pairs them up. A window becoming active in Windows, and a report saying "I just got focus"
within 1.5 s, are treated as the same window. Otherwise the window title must contain the report's workspace name.
A pairing that the window title contradicts is discarded. An untitled workspace containing a single folder, which
is what VS Code's "Duplicate As Workspace in New Window" creates, counts as that folder.

**Without it.** The app parses VS Code's default title, `[●] file - folder [- profile] - Visual Studio Code`. It
confirms the folder part against the folders VS Code itself lists as open in `storage.json`. A full path, and
with it your per-path alias, is attached only when exactly one known folder has that name.

Mechanism details and edge cases: [IMPLEMENTATION_NOTES.md → Window identity](../IMPLEMENTATION_NOTES.md#window-identity).

## The companion extension

| | |
| --- | --- |
| ID | `projectpreviews.project-previews-companion` 1.0.0 |
| Source | `companion/extension.js`, `companion/package.json` (plain JavaScript, no dependencies) |
| Package | `publish\ProjectPreviews\companion\project-previews-companion.vsix`, built offline by `tools\pack-companion.ps1` |
| Runs | In each local VS Code window (`extensionKind: ui`), after startup (`onStartupFinished`). Allowed in Restricted Mode. |
| Writes | `%LOCALAPPDATA%\ProjectPreviews\windows\<session>.json`: workspace name, workspace file path, folder paths/URIs, active tab name, focused flag and timestamps, extension-host process ID |
| Updates on | Window focus changes, tab changes, active editor changes, workspace folder changes |
| Never | Reads file contents, terminal output or chat. Makes no network requests. |

Installing:

- **From Settings**: the **Install companion extension** button runs `code --install-extension <vsix> --force`.
  It looks for `code.cmd` in the per-user install, the per-machine install, then `PATH`. If it can't find it, it
  says so.
- **Manually**: in VS Code, run *Extensions: Install from VSIX…* and pick the `.vsix`. Or run
  `code --install-extension "publish\ProjectPreviews\companion\project-previews-companion.vsix"`.

When it was installed during testing, all already-open windows started reporting within a few seconds, without a
reload. If a window still isn't identified, run *Developer: Reload Window* in it.

Uninstall: `code --uninstall-extension projectpreviews.project-previews-companion`, or from the Extensions view.

## Taskbar integration

Windows doesn't let one app replace another app's taskbar thumbnails: `DwmSetIconicThumbnail` and
`DwmSetIconicLivePreviewBitmap` only work for windows the calling process owns. Turning off Explorer's previews
globally (for example with `ExtendedUIHoverTime` or `TaskbarNoThumbnail`) would affect every app, so it isn't done.
Nothing is injected into Explorer or VS Code.

What the app does instead:

1. A low-level mouse hook (`WH_MOUSE_LL`, which runs inside this app) notices the pointer resting on the taskbar.
2. UI Automation identifies the button under it. Windows 11 exposes VS Code's button with
   `AutomationId = "Appid: Microsoft.VisualStudioCode"`. A name match ("Visual Studio Code - …") is the fallback
   for older taskbars.
3. The app shows its own topmost, non-activating popup above the button. While open, the popup also covers the
   button.
4. About 300 ms and 500 ms after the pointer came to rest, the app sends an invisible 1-pixel mouse move and moves
   it back. Explorer only cancels its own pending preview when it sees the pointer move off its button, and a
   resting pointer never produces that move.

The full reasoning, timings, and the approaches that didn't work are in
[IMPLEMENTATION_NOTES.md → Taskbar integration](../IMPLEMENTATION_NOTES.md#taskbar-integration). This depends on
Explorer behavior measured on one Windows build. A future Windows update could change it; the keyboard shortcut
and tray icon would still work.

## Interaction details

| Input | Behavior |
| --- | --- |
| Pointer rests on the VS Code taskbar button | Picker opens after ~0.12–0.22 s (measured). It doesn't take keyboard focus. |
| Pointer leaves the picker, its connecting strip, and the button | Picker closes after 0.3 s. |
| Click a card | Activates that window, restoring it if minimized, and closes the picker. |
| Left-click the (covered) VS Code button | One VS Code window: minimizes it if it's in front, otherwise brings it forward, like the taskbar does. Several windows: the picker takes keyboard focus instead of opening Explorer's list. |
| Right-/middle-click the covered button | The picker closes and the click is passed to the real button (jump list / new window). |
| Esc while hovering | Closes the picker. Esc is captured system-wide only while the hover picker is open. |
| Shortcut (default **Ctrl+Alt+Space**) | Opens the picker centered on the monitor with the pointer, focused on the most recent VS Code window that isn't already in front. Pressing it again closes it. Must include Ctrl, Alt or Win. If another app already uses it, Settings says so. |
| Left-click the tray icon | Same as the shortcut. |
| Arrow keys / Tab / Home / End | Move between cards (picker focused). |
| Enter or Space | Switch to the focused card. |
| Clicking elsewhere | Closes the picker. |
| Windows blocks the switch | The picker shows "Windows didn't allow the switch…" and the window's taskbar button flashes. The app doesn't report a switch that didn't happen. |

Card order stays the same while the picker is open. Closed windows disappear, new ones are added at the end, and
title or active-file changes update in place.

**Disable** (tray or Settings) immediately closes the picker, removes the mouse hook, the shortcut and
foreground-change tracking, and clears the in-memory previews. The tray icon stays. **Exit** does the same, then
removes the tray icon and ends the process. The app also closes the picker when Explorer restarts, the display
configuration changes, or the PC goes to sleep, and reinstalls the mouse hook after resume.

Accessibility: every card and control has an accessible name. When Windows' *Animation effects* is off, the fade
and hover zoom are skipped. High-contrast mode uses system colors (untested).

## Window previews

- Captured with `PrintWindow` only while the picker is open, after it appears, one window at a time on a
  background thread.
- Downscaled to 560 px wide, blurred once, kept **in memory only** per window, never written to disk. Dropped when
  windows close and when the app is disabled.
- Minimized windows, and captures that come back blank, show a colored card instead of a black or wrong image.

## Tested configurations

Tested: Windows 11 10.0.26200, one 2880×1620 monitor at 200% scaling, bottom centered taskbar with combined
buttons, VS Code 1.144, 4–11 VS Code windows, with and without the companion. Plain folders, a saved multi-root
workspace, an empty window, the same folder twice, and two different folders with the same name were all covered.
Light, dark and system (dark) themes.

**Not tested:** multiple monitors, mixed scaling, Windows 10, top/left/right or auto-hide taskbars, uncombined
taskbar buttons, a real Explorer restart (only simulated), sleep/resume, display changes, high contrast, screen
readers beyond accessible names, VS Code running as administrator, and having only one VS Code window open
(including the one-window click toggle).

Results and measurements are in [IMPLEMENTATION_NOTES.md → Validation](../IMPLEMENTATION_NOTES.md#validation).

## Building and testing

Needs the **.NET 9 SDK** and **PowerShell 7** (`pwsh`). Exit Project Previews first; the exe is locked while it
runs.

```powershell
pwsh tools\build.ps1                      # companion .vsix → unit tests → Release single-file exe → dist\*.zip
dotnet test tests\ProjectPreviews.Tests   # unit tests only
pwsh tools\pack-companion.ps1             # rebuild only the companion .vsix
powershell -File tools\make-icon.ps1      # regenerate src\ProjectPreviews\Assets\app.ico
```

Project layout:

```
src/ProjectPreviews/   WPF app (Core = pure logic, Services, Platform = Win32, UI)
tests/                 xUnit tests for alias, identity, settings, title, binding, shortcut and blur logic
companion/             VS Code extension source
tools/                 build, pack and icon scripts
docs/                  this reference and README screenshots
```

## Diagnostics

Set `PROJECT_PREVIEWS_DEBUG=1` in the environment before starting the app to append hover timing (dwell, UI
Automation result, show time, nudges) to `%LOCALAPPDATA%\ProjectPreviews\debug.log`. It's off by default.

```powershell
$env:PROJECT_PREVIEWS_DEBUG = '1'; & .\publish\ProjectPreviews\ProjectPreviews.exe
```

## Privacy and network

No accounts, sign-in, analytics, telemetry, network access or AI calls, in either the app or the companion
extension. Data stays in the files listed above. Previews are kept in memory only.
