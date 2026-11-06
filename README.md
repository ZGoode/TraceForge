# TraceForge

A local-first requirements and traceability tool for Windows, built with WPF and .NET 10. No server, no subscription, no data leaves your machine. Each project is a folder you can back up, copy, or archive like any other files.

## Concepts

You build a tree of **folders and modules**. Each module holds an outline of **objects** (requirements and grouping headings). A module nested under another is the next level down, e.g. Stakeholder Needs → System Requirements → Design → Tests. **Links** run from one level to the next.

- **Local-first:** a project is a folder on disk.
- **History is kept:** every change adds a revision; baselines freeze a module at a point in time.
- **Recoverable:** deletes are soft; permanent deletion is a separate, confirmed step.

## Features

- Folder/module tree with rename, reorder, move, delete, and restore
- Outlines with typed attributes: text, integer, real, boolean, date, enumeration
- Validated cell editing, natural sort, filters, saved views
- Copy/paste sections; undo/redo up to 100 steps
- Links with arrows, trace tree, and suspect-link review
- Impact analysis (upstream/downstream), coverage checks, and gap reports
- Full revision history per object; restore an old revision as a new one
- Baselines (a module or a module plus everything under it), with comparison to the current state or another baseline
- Review comments, attachments (SHA-256 checked), embedded images and tables
- CSV (with preview and spreadsheet-formula guard), ReqIF, and standalone HTML report import/export
- Integrity check, audit log, backup/restore, unused-attachment cleanup
- Built-in searchable user guide (**F1**)

## Install

TraceForge is currently built from source. The published build is self-contained and runs on any 64-bit Windows PC with nothing else installed.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. In the folder containing `TraceForge.csproj`, run:

   ```
   dotnet publish TraceForge.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o .\publish
   ```

3. Copy the `publish` folder anywhere and run `TraceForge.exe`.

> **Important:** Don't enable trimming (`PublishTrimmed`). WPF doesn't support it and the app will crash on startup.

## Links and Traceability

A link can only go from an object to an object in a **direct child module**; the store enforces this. When an object changes, links to it become **suspect** until reviewed. Suspect-link review, impact analysis, and gap reports all build on this.

## Safety and Data Protection

- **Soft delete:** deleted modules, folders, and objects stay in the project and can be restored.
- **Permanent delete:** only works on already-deleted items. You must type the name to confirm, you're warned if links to remaining modules would be lost, and it's refused if the module has a baseline.
- **Backup/restore:** File → Back Up Project saves a single zip. Restore always goes to a new folder, never overwriting.
- **Upgrades:** older projects are upgraded automatically on open, after a safety copy of the database is saved to the project's backups folder.
- **Integrity check:** verifies the database, references, outlines, revision history, attribute values, and attachment hashes.

> **Important:** Once a project is upgraded (database schema 10), older TraceForge versions can't open it. Keep a backup if you need them.

## Development

**Requirements:** Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and any .NET-capable editor.

```
dotnet build                          # debug build
dotnet run                            # start the app
dotnet test TraceForge.sln            # run tests
dotnet build TraceForge.sln -c Release
```

Debug builds are for development only; use `dotnet publish` (above) to produce a distributable copy.

### Project structure

| Path                            | Contents                                                                                                                         |
|---------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| `src/TraceForge.Domain`         | Core logic with no file or database access: outlines, import planning, markup, coverage, tracing, snapshots, reports, ReqIF      |
| `src/TraceForge.Application`    | Contracts for the project store and package service, plus the undo stack                                                         |
| `src/TraceForge.Infrastructure` | SQLite store (one partial-class file per area), package service, diagnostic log                                                  |
| Solution root                   | WPF app: `MainWindow*.cs`, `Workspace.cs`, `Dialogs.cs`, `HelpGuide.cs`, and `Assets/` (the icon)                                |
| `tests/`                        | xUnit tests for the domain and infrastructure layers                                                                             |
| `docs/PROJECT_FORMAT.md`        | Project folder format, schema history, and lifecycle rules                                                                       |

A project is a `.traceproj` folder containing `project.json`, `project.db` (SQLite), and `attachments/`. Outside a project, TraceForge writes only to `%LocalAppData%\TraceForge\` (`ui.json`, `diagnostics.log`, `crash.log`); Help → Open Log Folder goes there.

### Design rules

- Siblings are ordered by `sort_order`, then name (case-insensitive), then id. The store and app must agree.
- Live siblings can't share a name (case-insensitive).
- Audit events, revisions, baselines, and comments are append-only, except when a module is permanently deleted.
- Schema changes need a migration, and `SqliteProjectStore.LatestSchemaVersion` and `ProjectManifest.CurrentSchemaVersion` must be bumped together (a test enforces this).
- Update `HelpGuide.cs` and `docs/PROJECT_FORMAT.md` whenever a feature or the format changes.

## Known Limitations

- Delete Permanently can't be undone and clears the undo history, which only lasts for the current session.
- A deleted module stays live by design: its objects still appear in global lists, search, reports, and link pickers until permanently deleted.
- Permanent delete leaves the module's attachment files behind until you run File → Clean Up Unused Attachment Files.
- Zipped `.reqifz` files and ReqIF relations can't be imported yet.

## References

- [.NET](https://dotnet.microsoft.com/)
- [Windows Presentation Foundation](https://learn.microsoft.com/dotnet/desktop/wpf/)
- [SQLite](https://www.sqlite.org/)
- [ReqIF (OMG)](https://www.omg.org/spec/ReqIF/)

## License

Shield: [![CC BY-NC-SA 4.0][cc-by-nc-sa-shield]][cc-by-nc-sa]

This work is licensed under a
[Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License][cc-by-nc-sa].

[![CC BY-NC-SA 4.0][cc-by-nc-sa-image]][cc-by-nc-sa]

[cc-by-nc-sa]: http://creativecommons.org/licenses/by-nc-sa/4.0/
[cc-by-nc-sa-image]: https://licensebuttons.net/l/by-nc-sa/4.0/88x31.png
[cc-by-nc-sa-shield]: https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg
