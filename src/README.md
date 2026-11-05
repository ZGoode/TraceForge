# TraceForge

A local-first requirements and traceability workbench for Windows (WPF, .NET 10). Projects are ordinary folders on your computer; nothing is sent anywhere.

## Build and run

```powershell
dotnet run                          # start the desktop app (run from this folder)
dotnet test TraceForge.sln          # run the automated tests
dotnet build TraceForge.sln -c Release
```

Requires the .NET 10 SDK on Windows. In the app, press **F1** (or Help, User Guide) for the built-in guide.

## What it does

- **Modules and levels.** Folders and modules form a tree. A module nested under another module is the next document level (for example Stakeholder Needs, System Requirements, Design, Tests). Rename, reorder (Alt+Up/Down or drag and drop), move, delete and restore modules and folders, all undoable; permanent deletion is a separate, confirmed step for items already deleted, with a warning about links to modules that stay.
- **Objects and outline.** Hierarchical outline with headings and typed attributes (text, integer, real, boolean, date, enumeration), required-value markers, validated cell editing, natural sorting, filters and saved views, copy and paste of whole sections, soft delete with restore, and undo/redo (up to 100 steps per session).
- **Links and traceability.** A link may only go from an object to an object in a *direct child module*; the store enforces this. Link pickers, link arrows, a trace tree, suspect-link review, impact analysis (upstream and downstream), coverage summary and traceability-gap reports.
- **History.** Every change to an object adds an immutable revision (restorable as a new revision). Baselines freeze a module, or a module and all its children, and can be compared with the current state or with each other.
- **Comments and attachments.** Review comment threads with resolve/reopen, attachments (SHA-256 verified, 50 MB limit, risky file types never launched), and pictures and tables embedded in text.
- **Import and export.** CSV (with preview, one undo step and a spreadsheet formula guard), ReqIF, a self-contained HTML report, and a traceability-gap CSV.
- **Safety.** Integrity check (including a warning for duplicate sibling names in old projects), unused-attachment cleanup, append-only audit log, automatic pre-migration database copy, File, Back Up Project and Restore Project from Backup (a restore always goes to a new folder), and a diagnostic log.

## Projects

A project is a `.traceproj` folder holding `project.json`, `project.db` (SQLite) and `attachments/`. Keep projects outside the folder TraceForge is installed in. The format, schema history and lifecycle rules are in [`docs/PROJECT_FORMAT.md`](docs/PROJECT_FORMAT.md). A project upgraded by this version (schema 10) cannot be opened by older versions.

## Code layout

| Path | Contents |
|---|---|
| `src/TraceForge.Domain` | Pure logic with no I/O: outline, import planning, markup, coverage, tracing, snapshots, reports, ReqIF |
| `src/TraceForge.Application` | The `IProjectStore` and `IProjectPackageService` contracts and the undo stack |
| `src/TraceForge.Infrastructure` | SQLite store (`SqliteProjectStore*.cs`, one partial-class file per area), package service, diagnostic log |
| Solution root | The WPF app: `MainWindow*.cs`, `Workspace.cs`, `Dialogs.cs`, `HelpGuide.cs`, `Assets/` (icon) |
| `tests/` | xUnit projects for the domain and infrastructure layers |

Design rules worth keeping:

- Sibling order is `sort_order`, then name (case-insensitive), then id. The store (`OrderedSiblings`) and the app (`Workspace.InTreeOrder`) must always agree.
- Two live siblings may not share a name (case-insensitive).
- Audit events, revisions, baselines and comments are append-only everywhere except a permanent module delete.
- To change the database, add a migration and bump `SqliteProjectStore.LatestSchemaVersion` and `ProjectManifest.CurrentSchemaVersion` together (a test enforces that they match).
- Update `HelpGuide.cs` and `docs/PROJECT_FORMAT.md` whenever a feature or the format changes.

## Files TraceForge writes outside a project

All in `%LocalAppData%\TraceForge\`: `ui.json` (View menu choices), `diagnostics.log` (one JSON line per event; your user profile path is replaced by `%USERPROFILE%`) and `crash.log` (unhandled errors). Help, Open Log Folder opens this folder.

## Known limits

- Delete Permanently is not undoable and clears the undo history. Undo history is per session (up to 100 steps).
- A module marked deleted stays live on purpose, so its objects still appear in global lists, search, reports and link pickers until it is permanently deleted.
- Permanent delete leaves the module's attachment files in `attachments/` until File, Clean Up Unused Attachment Files removes them.
- Zipped `.reqifz` files and ReqIF relations are not imported.
