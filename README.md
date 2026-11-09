# Project Previews

Hover the VS Code taskbar icon and see your open VS Code windows as cards with **big, readable project
names** instead of tiny truncated titles. Click a card to switch to that exact window.

- Large project alias, centered; the active file's name underneath.
- Blurred, darkened live preview of each window behind it (adjustable), and a project color accent.
- Two windows of the same project get "1 of 2" / "2 of 2" badges; different folders that share a name show
  their parent folder.
- Aliases come from Claude Workers (AI Status Indicator) automatically; you can override any of them.
- Light, dark or system theme. Keyboard-friendly. VS Code only (v1).

## Requirements

- Windows 10 or 11 (developed and tested on Windows 11), x64.
- .NET 9 Desktop Runtime. It is already installed on this PC. Elsewhere, Windows offers to download it on first launch.
- VS Code (stable). The companion extension is optional but recommended (see below).
- No administrator rights.

## Launch

Run `publish\ProjectPreviews\ProjectPreviews.exe` (or unzip `dist\ProjectPreviews-1.0.0-win-x64.zip` anywhere
and run the exe). It lives in the tray. The first launch opens Settings.

Launching it again while it's running opens the running copy's Settings (it's single-instance).

## Use it

| Action | What happens |
| --- | --- |
| Rest the pointer on the VS Code taskbar icon | The picker opens above it (after ~0.1 s). It doesn't take keyboard focus. |
| Click a card | That window comes to the front, restored if minimized. |
| Click the VS Code icon while the picker is open | One window: toggles it like the taskbar does. Several: the picker takes keyboard focus. |
| Right-/middle-click the VS Code icon | Goes to the real taskbar button (jump list / new window). |
| **Ctrl+Alt+Space** (configurable) | Opens the picker in the middle of the screen, focused on the most recent other VS Code window. |
| Left-click the tray icon | Same as the shortcut. |
| Arrow keys, Home/End, Tab | Move between cards (when the picker has focus). |
| Enter / Space | Switch to the focused card. |
| Esc, clicking elsewhere, or moving the pointer away | Closes the picker. |

### Tray menu

**Disable previews / Enable previews**, **Open window picker**, **Settings…**, **Exit**.
Disabling closes the picker and removes the mouse hook and the shortcut immediately; the taskbar behaves exactly as
normal. Exit does the same and removes the tray icon.

### Settings

- Enable project previews; Start with Windows (off by default).
- How to open the picker: taskbar hover and/or a keyboard shortcut (click the box and press the new combination).
- Theme (System / Light / Dark), blur and darkness, with a live sample card.
- **Projects and aliases**: every open project (and every project you've customized), where its name comes from,
  an alias box, an accent color, and Reset.
- Companion extension status and an Install button.

Settings are saved to `%APPDATA%\ProjectPreviews\settings.json`. If that file is ever damaged, it is copied to
`settings.corrupt-<date>.json` before defaults are used, and Settings shows a notice.

## Aliases

A window's name is chosen in this order:

1. **Your alias** from Settings. It's tied to the exact folder or `.code-workspace` path, so two folders that are
   both called `Website` can have different aliases.
2. **Claude Workers' alias**: `projectAliases` in `%USERPROFILE%\.claude\status-indicator\config.json`, matched by
   folder name the same way Claude Workers matches it (case-insensitive). Read-only; Claude Workers doesn't need
   to be running and its file is never changed. Edits there show up the next time the picker opens.
3. **The folder or workspace name**, with underscores shown as spaces (Claude Workers' rule).

Empty windows show "Empty window". If a window's project can't be identified (no companion extension and an
ambiguous or custom title), the card shows the name from the title and the subtitle says so; your path-based
aliases are never applied to a guess.

## Companion VS Code extension (recommended)

VS Code's window title is the only identity Windows exposes, and it only contains the folder's *name*. The tiny
companion extension (`companion/`, ~80 lines of plain JavaScript, no dependencies) lets each VS Code window report
its exact folder/workspace paths and the **name** of its active tab to
`%LOCALAPPDATA%\ProjectPreviews\windows\<window>.json`. It never reads file contents, terminals or chats, makes no
network calls, and deletes its file when the window closes.

Install: Settings → **Install companion extension** (runs `code --install-extension`), or in VS Code run
"Extensions: Install from VSIX…" and pick `publish\ProjectPreviews\companion\project-previews-companion.vsix`.
Open windows pick it up within a few seconds; no reload needed.

Without it everything still works, but identity comes from window titles plus the folder list in VS Code's own
`storage.json` (read-only), so identically named folders can't be told apart and per-path aliases only apply
when the name is unambiguous.

## Limitations

- **Taskbar hover is an overlay, not a replacement of Windows' thumbnails.** Windows doesn't let one app replace
  another app's taskbar thumbnails (`DwmSetIconicThumbnail` only works for your own windows). Project Previews
  shows its own popup and briefly covers the VS Code button so Explorer cancels its own preview. See
  `IMPLEMENTATION_NOTES.md` for how, and what was measured.
- If Explorer's preview was already opening (e.g. you rested on the icon while Project Previews was starting),
  you can see it once; moving the pointer off and back fixes it.
- Only the bottom Windows 11 taskbar on a single 200% monitor was tested. Top/left/right (Windows 10) taskbars,
  secondary-monitor taskbars and mixed DPI are handled in code but untested.
- Windows on other virtual desktops aren't listed (the taskbar doesn't show them either).
- VS Code Insiders, VSCodium and Cursor are not included in v1.
- If Windows refuses to switch windows (foreground lock), the picker says so and flashes the VS Code button
  instead of pretending it worked.

## Turn it off / uninstall

- **Off for now**: tray icon → Disable previews (or Exit).
- **Uninstall**:
  1. Tray icon → Exit.
  2. If you turned on "Start with Windows", turn it off first in Settings (or delete the `ProjectPreviews` value
     under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
  3. Delete the app folder.
  4. Delete `%APPDATA%\ProjectPreviews` (settings) and `%LOCALAPPDATA%\ProjectPreviews` (window reports).
  5. In VS Code, uninstall "Project Previews Companion" (or `code --uninstall-extension projectpreviews.project-previews-companion`).

Nothing else on the system is changed.

## Build

```powershell
pwsh tools\build.ps1        # companion .vsix, tests, Release single-file exe, dist\*.zip
dotnet test tests\ProjectPreviews.Tests
```

Diagnostics: set `PROJECT_PREVIEWS_DEBUG=1` before launching to log hover timing to
`%LOCALAPPDATA%\ProjectPreviews\debug.log`. Off by default.

## Privacy

No accounts, network access, analytics or AI calls. Window previews are captured only while the picker is open,
kept in memory (downscaled), never written to disk, and cleared when you disable the app.
