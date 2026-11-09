# Implementation notes

## Stack

C# / WPF on .NET 9, framework-dependent single exe (~0.5 MB) plus a ~3 KB VS Code extension.
The app is mostly Win32 integration (low-level mouse hook, WinEvents, UI Automation, PrintWindow, foreground
rules, hotkeys, DPI), which .NET reaches directly through P/Invoke. WPF provides GPU blur, transparent layered
windows, per-monitor DPI, and accessibility. .NET 9's Fluent `ThemeMode` gives light/dark/system theming to
standard controls, menus and tooltips. Electron would have needed native add-ons for every hook and ~150 MB.
Idle cost is a tray icon, a hook thread and a WinEvent hook (numbers below).

```
src/ProjectPreviews/
  Core/         Identity (aliases, title parsing), Companion (reports + window binding), Settings   ← unit tested
  Services/     WindowCatalog: enumerates VS Code windows, binds identity, resolves aliases
  Platform/     Native (P/Invoke), TaskbarHover (mouse hook + UIA), Capture, Shell (activation, startup, shortcut)
  UI/           PickerWindow, SettingsWindow, Cards, ThemeManager
  App.xaml.cs   lifecycle: tray, hover state machine, shortcut, environment events
companion/      VS Code extension (plain JS) + tools/pack-companion.ps1 (builds the .vsix offline)
```

## Taskbar integration

**Constraint.** `DwmSetIconicThumbnail`/`DwmSetIconicLivePreviewBitmap` only work on windows owned by the calling
process, so a separate utility can't replace VS Code's native thumbnails. Hiding Explorer's popup globally
(e.g. `ExtendedUIHoverTime`, `TaskbarNoThumbnail`) would change Explorer settings for every app, and was ruled out.
No code is injected into Explorer or VS Code.

**What it does:**

1. **Detect the hover.** A `WH_MOUSE_LL` hook (a low-level hook runs in our own process, no DLL injection) on a
   dedicated thread only compares the cursor with the taskbar rectangles and forwards moves that are over the
   taskbar. When the pointer rests for 120 ms, **UI Automation** finds the button under it. On Windows 11 each
   taskbar button has `AutomationId = "Appid: <AppUserModelID>"`; VS Code's is
   `Appid: Microsoft.VisualStudioCode` (button name such as "Visual Studio Code - 4 running windows" as a fallback).
   `AutomationElement.FromPoint` stops at the taskbar's XAML host window, so we start from the
   `Shell_TrayWnd`/`Shell_SecondaryTrayWnd` handle instead. The button position is located as soon as the pointer
   enters the taskbar and cached for 4 s; a cached hit is re-checked in the background and the popup closes if
   the buttons have shifted.
2. **Show our popup without taking focus.** It's a layered, topmost, `WS_EX_NOACTIVATE` window placed above the
   button within the monitor's work area. A nearly invisible "bridge" (alpha 1) connects button and cards so the
   pointer can travel between them, and a subtle tab covers the VS Code button itself.
3. **Stop Explorer's preview from opening on top.** This is the core finding. Measured on this PC, Explorer
   opens its thumbnail popup about 780 ms after the pointer rests on a button. It cancels that only when it sees
   a mouse move outside the button, and it ignores leaves in roughly the first 250 ms. A perfectly still pointer
   never produces that move, and Explorer also raises its (topmost) taskbar during the hover. So while our popup
   covers the button, a dedicated thread re-asserts our topmost position and, once `WindowFromPoint` confirms our
   popup is really under the pointer (a layered window is click-through until painted), injects a **1-pixel move
   and back** (absolute coordinates, so pointer acceleration can't leave it offset). This happens about 300 ms and
   500 ms after the pointer came to rest. Explorer then cancels its own timer. No Explorer setting is touched.
4. **Make the first show fast.** The first show of a WPF window like this costs ~0.5 s (fonts, shaders,
   layered-window setup). An invisible off-screen warm-up pays that at startup. Cards are pre-built when the
   pointer enters the taskbar, and preview captures are deferred 700 ms in hover mode so repainting can't delay
   the first frame.

**Clicking the covered VS Code button:** left-click with one window toggles it (minimize if it's in front,
otherwise activate), like the taskbar. With several windows, the picker takes keyboard focus instead of opening
Explorer's list. Right/middle clicks hide the popup and are re-sent to the real button (jump list / new window).
The popup won't reopen until the pointer leaves the button.

**Fallbacks that always work:** configurable global shortcut (`RegisterHotKey`, default Ctrl+Alt+Space) and
tray-icon left-click open the same picker, centered and focused.

**Dismissal:** pointer outside the panel/bridge/button for 300 ms, Esc (registered as a hotkey only while the
hover popup is open, since it has no focus), clicks outside (reported by the mouse hook while open), losing
activation (keyboard mode), Explorer restart (`TaskbarCreated`), display changes, sleep.

## Window identity

`EnumWindows` → visible, uncloaked, unowned, non-tool windows whose process image is `Code.exe` (z-order =
most recent first). Then, per window:

1. **Companion report** (preferred). The extension can't see its own HWND, so reports are bound to windows by
   **focus timing**: a `EVENT_SYSTEM_FOREGROUND` WinEvent for a VS Code window and a report saying "I gained focus"
   within 1.5 s belong together. Bindings stick until the window or report goes away. A single `focused` report
   binds to the current foreground window. Unbound reports fall back to **title matching**: the workspace name
   must be a whole `" - "`-separated part of the title, and a matching active file adds confidence. Reports
   whose extension-host process is gone are deleted.
2. **Title only** (no companion): the default title `[●] file - root [- profile] - Visual Studio Code` is parsed.
   The root part is confirmed against folders VS Code lists as open in its own `storage.json` (read-only, re-read
   only when it changes). A full path, and so a per-path alias, is attached only if exactly one known folder has
   that name. Otherwise the identity is marked uncertain and only name-based aliasing applies.

Identity kinds: folder, saved `.code-workspace` (keyed by the workspace file), untitled multi-root (name only),
empty window. An untitled workspace with a single folder (VS Code's "Duplicate As Workspace in New Window") counts
as that folder, so the two windows show as "1 of 2" / "2 of 2" of the same project.

Paths are normalized (full path, `\`, no trailing separator, lower-case) before keying. `file:///c%3A/...` URIs from
VS Code are decoded manually because `Uri.LocalPath` mangles the escaped colon. A unit test caught this.

## Previews

`PrintWindow(PW_RENDERFULLCONTENT)` into a DIB, `StretchBlt` (HALFTONE) down to 560 px wide, blurred once with a
3-pass box blur (≈Gaussian) on the capture thread, kept in an in-memory
dictionary by HWND. Taken only while the picker is open, sequentially on a background thread. Minimized windows
and blank captures get a styled accent-gradient card, never a black or stale-from-another-window image. Captures
are dropped when windows close or the app is disabled.

## Lifecycle

Event-driven, no polling while idle. Timers run only while the picker is open (80 ms leave check) or during a
hover (dwell). Live updates while the picker is open use WinEvent hooks scoped to VS Code's process
(destroy/show/name change/minimize) with 150 ms debounce, keeping card order stable. Settings writes are atomic
(temp file + move).

## Validation (2026-11-08, Windows 11, single 2880×1620 monitor at 200%, 4–11 VS Code windows)

**Automated:** 51 unit tests (alias precedence, path normalization and URI decoding, Claude Workers parsing,
settings recovery/clamping/round-trip, title parsing, report→identity, focus/title binding, duplicate
disambiguation, shortcut parsing, taskbar-button matching, blank-capture detection). All pass.

**Interactive, driven with real `SendInput` mouse/keyboard input and checked by sampling windows every 15 ms,
UI Automation and screenshots:**

| Check | Result |
| --- | --- |
| Hover shows our popup | ~110–150 ms after the pointer rests (cold start ~150 ms after warm-up) |
| Explorer's native popup suppressed | 12/12 (4 cold starts × 3 hovers, 9 windows), 8/8, 5/5 in later runs. Earlier builds failed; see "what didn't work" |
| Focus not taken by hover | Foreground unchanged in every hover run |
| Pointer from button into cards | Popup stays open along the path; closes ~300 ms after leaving |
| Card click activates exact window | Website (minimized → restored), Portal (Acme vs archive), demo workspace |
| Keyboard | Shortcut opens focused picker; arrows move focus; Enter switches; Esc closes |
| Esc during hover (no focus) | Closes; foreground unchanged; doesn't reopen while resting on the button |
| Outside click | Closes in hover and keyboard modes |
| Click covered VS Code button (several windows) | Picker takes focus |
| Right-click covered button | Popup closes; VS Code's real jump list opens |
| Aliases | Claude Workers alias (`Toah_Nipi_Website` → Website), folder names, workspace name (demo), empty window |
| Same project twice | "Website 1 of 2" / "2 of 2" with different active files |
| Identical folder names | Two `Toah_Nipi_Portal` folders: "Portal · Acme" vs "Portal · archive"; per-path override "Acme Portal" applied to one only |
| Alias persistence | Set in Settings → saved by full path → survived app restart |
| Themes | Dark (system) and Light, for settings and popup; switch applies live |
| Blur/darkness | Sliders update sample card live and persist; popup uses them |
| Window closed while open | Card removed, others keep their order, picker stays open |
| Explorer restart | Simulated by posting `TaskbarCreated` to the app: picker closes. Real Explorer restart not performed (it would have closed your open File Explorer window) |
| Single instance | Second launch exits and opens the running copy's Settings |
| Disable | No popup, native preview works normally, shortcut inert, mouse hook silent |
| Exit (tray menu) | Process ends; taskbar hover shows Explorer's normal preview |
| Settings fits screen | Fixed: was taller than the 200% work area |
| Empty window → opens a folder | Found live (LendLog): old report briefly won. Fixed: title-contradicted bindings are dropped (unit test added) |
| Idle CPU | 0–31 ms CPU per 60 s idle (≈0%) |
| Memory | ~125–140 MB private at start; ~165 MB after hovers with 11 windows. Hardware rendering was ~425 MB, so the picker renders in software with pre-blurred previews (Explorer suppression re-verified: 6/6 and 9/9) |

**What didn't work along the way (and why the design is what it is):** showing the popup without a nudge (Explorer
popup appeared on top 5/6 times); nudging immediately (ignored by Explorer); nudging from the UI thread or thread
pool (arrived late); nudging without re-asserting topmost (Explorer had raised its taskbar above us); first show
without warm-up (~0.5–0.8 s, Explorer won).

**Not verified:** multiple monitors and mixed DPI (only one monitor available), Windows 10 / non-bottom taskbars,
sleep/resume, real display-configuration changes, a real Explorer restart, auto-hide taskbar, high-contrast mode,
screen readers beyond UIA names, and long-term reliability across Windows updates (the hover technique depends on
current Explorer behavior, measured on this build: Windows 11 10.0.26200).
