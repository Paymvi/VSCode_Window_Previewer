# Project Previews Companion

Helper for the **Project Previews** Windows app. For each VS Code window it writes one small JSON file to
`%LOCALAPPDATA%\ProjectPreviews\windows\` containing:

- the workspace name, workspace file path and folder paths,
- the *name* of the active tab (for example `index.ts`),
- whether the window is focused and when it last gained focus.

It never reads file contents, terminal output or chat, and never uses the network. The file is deleted when
the window closes. Uninstall it like any other extension.
