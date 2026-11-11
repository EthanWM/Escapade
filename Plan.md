# Escapade — Concept & Feature Plan

A personal, keyboard-first, dual-pane file manager for Windows, built for dev work.

## Concept

Escapade is a single-window, dual-pane file manager organized around **workspaces**: named, persistent sets of locations (typically one per repo or project). The app always knows everything you have open, so you never reopen a folder or hunt for a tab.

The central problem it solves is losing track of where things are, not raw speed or mouse dependence. Those get fixed along the way.

### Core ideas

1. **One window, never many.** Tabs live inside workspaces, and switching workspaces is instant. Every pane, tab, scroll position and selection is restored between sessions, so closing the app costs nothing.
2. **One jump command.** Folders, files in open tabs, bookmarks, recent places and commands are all reachable from a single fuzzy palette. Forgot which tab a file is in? Type part of its name and jump to it.
3. **Two panes make moves trivial.** The opposite pane is always the default destination (Total Commander style), so moving files takes a keystroke.

## Features by pain point

### Tab and window sprawl, reopening folders, losing files

- **Workspaces with full session restore.** Opening a folder inside a git repo can offer to create or switch to that repo's workspace automatically.
- **Duplicate detection.** Navigating to a folder already open in another tab jumps to that tab instead of opening a duplicate.
- **Go-to-anything (`Ctrl+P`).** Searches across open tabs, bookmarks and frecency-ranked history (scored by how often and how recently you visit, like zoxide). Covers both "where did I have that open" and "take me to the folder I always use."
- **Tab overview (`Ctrl+Tab`).** Every tab in every workspace as one filterable list.

### Too mouse-heavy

- **Commander-style keys:** `Tab` switches panes, `F5` copies to the other pane, `F6` moves, `F2` renames, `Ctrl+Shift+N` creates a folder.
- **Type-to-filter.** Typing in a pane narrows the list immediately. `Esc` clears it.
- **Command palette (`Ctrl+Shift+P`).** Every action with its shortcut shown, so bindings are learned through use.
- **Keybindings in a config file** that hot-reloads, so anything can be remapped without rebuilding.

### Moving files between places

- **Copy/move to the other pane by default**, using the shell's `IFileOperation` for native progress, conflict prompts and undo.
- **Recent destinations.** "Move to…" offers the last several places you sent files, ranked by frecency.
- **Shelf** *(later)*. A staging area: gather files from several folders, then drop them all in one place.

### Slow and laggy

- **Custom virtualized list** instead of the shell view, with background enumeration so huge folders (e.g. `node_modules`) never block the UI.
- **Icons cached per extension** and loaded lazily. Folder sizes computed only on request.

### Can't customize layout or views

- **Column sets and sort order** saved per workspace (or per folder).
- **Toggleable pane layouts:** side-by-side, stacked, or single full-width pane.

### Dev-specific extras

- **Git status column** (modified / untracked / ignored), with repo root and current branch shown in the path bar.
- **Gitignore-aware hiding.** One toggle hides `bin/`, `obj/`, `node_modules/` and anything else `.gitignore` excludes.
- **"Open here" actions** for VS Code, a terminal, or any configured tool.
- **Copy-path variants:** absolute, relative to repo root, forward slashes, quoted.
- **Reveal in Explorer.** The escape hatch for anything Escapade doesn't handle yet, such as the native context menu.

## Phased plan

Each phase produces something usable. The hard COM/shell work comes last, by which point it will be clear whether it's needed or whether *Reveal in Explorer* is enough.

| Phase | Goal | Contents |
|---|---|---|
| 0.1 | Browse fast | Single pane, background enumeration, keyboard navigation, path bar, open files via `ShellExecute`, type-to-filter |
| 0.2 | Daily-drivable | Dual pane, `F5`/`F6` operations via `IFileOperation`, rename, delete to Recycle Bin, Reveal in Explorer |
| 0.3 | Keyboard-first | Command palette, go-to-folder with frecency, bookmarks, keybinding config |
| 0.4 | The core idea | Workspaces, session restore, duplicate detection, tab overview, search across open tabs |
| 0.5 | Dev features | Git status, gitignore hiding, open-in actions, copy-path variants |
| Later | Shell integration | Native context menu, drag and drop with other apps, preview pane, shelf |

## Out of scope for v1

- Built-in file preview or editor
- File content search (use ripgrep from a terminal)
- Archive browsing
- Batch rename
- Cloud and phone (MTP) locations
- Thumbnails
- Replacing Explorer as the system default (Win+E, "Open containing folder", file dialogs)

## Technical notes

- **Stack:** WinForms on current .NET, with a `ListView` in virtual mode for the file panes.
- **File operations:** delegate to `IFileOperation` rather than writing a custom engine. It only works in a single-threaded apartment (STA), so keep calls on the UI thread or a dedicated STA thread.
- **Shell interop:** use a maintained P/Invoke wrapper library (e.g. Vanara) rather than hand-written COM declarations, and give agents the Microsoft docs as reference, since they tend to invent COM signatures.
- **Native context menu:** hosting `IContextMenu` is the hardest single feature. Raymond Chen's multi-part "How to host an IContextMenu" series on *The Old New Thing* is the reference.
- **Fallback option:** the `ExplorerBrowser` control (`IExplorerBrowser`) can host a real Explorer view inside a pane if a native view is ever wanted for a specific case.

## Open questions

- **Workspace model:** tied to a repo (auto-created, rooted at the repo, git-aware) or free-form (any user-chosen set of folders)? Possibly both, with repo workspaces as a specialization.