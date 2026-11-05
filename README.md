# TraceForge

A requirements and traceability tool for Windows that keeps everything on your own computer. It's built with **WPF and .NET 10**.

I wanted a way to manage requirements, trace them from one level of documents down to the next, and keep a full history of every change, without needing a server, a subscription, or sending project data anywhere. TraceForge is that tool.

Each project is just a folder. You can back it up, copy it, or archive it like any other files.

## About the Tool

TraceForge is organized the way most requirements document sets already are. You build a tree of **folders and modules**, and each module holds an outline of **objects**, which are your requirements and the headings that group them. A module nested under another module is the next level down, like Stakeholder Needs, then System Requirements, then Design, then Tests. **Links** run from one level to the next.

A few ideas shaped how it works:

- **Local-first.** A project is a folder on your computer, and nothing gets sent anywhere.
- **History stays put.** Every change to an object adds a new revision instead of overwriting the old one, and baselines freeze a module at a moment in time.
- **Mistakes are fixable.** Deleting something doesn't destroy it, and wiping it for good is a separate step that asks you to confirm.

## Download and Run

For now TraceForge is built from source with the .NET SDK. The build produces a standalone copy that runs on any 64-bit Windows PC without installing anything else.

### Building a Standalone Copy

1. Install the **.NET 10 SDK** on Windows.
2. Open a terminal in the project folder, the one with `TraceForge.csproj` in it.
3. Run:

```powershell
dotnet publish TraceForge.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o .\publish
```

4. Copy the `publish` folder wherever you want it.
5. Run `TraceForge.exe`.

> **Important:** Don't turn on trimming (`PublishTrimmed`). WPF doesn't support it, and the program will crash when it starts.

Once it's running, press **F1** (or go to Help, User Guide) to open the built-in guide.

## What You Can Do With It

- Build a tree of folders and modules, and rename, reorder, move, delete, and restore them (all of it can be undone)
- Write outlines with headings and typed attributes: text, integer, real, boolean, date, and enumeration
- Edit cells with validation, sort naturally, filter, and save views
- Copy and paste whole sections, and undo or redo up to 100 steps
- Link requirements from one level to the next, with link arrows, a trace tree, and suspect-link review
- See what a change will affect upstream and downstream, check coverage, and find traceability gaps
- Look back at the full revision history of any object and restore an old revision as a new one
- Freeze a module (or a module and everything under it) as a baseline, then compare it to the current state or to another baseline
- Leave review comments, attach files (checked with SHA-256), and embed pictures and tables in text
- Import and export CSV (with a preview and a guard against spreadsheet formulas), ReqIF, and a standalone HTML report
- Run an integrity check, read the audit log, back up and restore a project, and clean out unused attachment files
- Get help from a searchable user guide, a shortcuts list, and a Help, About dialog

## Links and Traceability

A link can only go from an object to an object in a **direct child module**. The store enforces that, so your trace structure always matches the levels of your documents.

When an object changes, the links to it can become **suspect** until someone reviews them. The suspect-link review, impact analysis, and gap reports all build on that.

## Safety and Data Protection

TraceForge is built so that mistakes can be undone.

- **Soft delete.** Deleted modules, folders, and objects stay in the project, and you can restore them.
- **Permanent delete** only works on things that are already deleted. You have to type the name to confirm, you get a warning if links to modules that are staying would be lost, and it's refused outright if the module has a baseline.
- **Backup and restore.** File, Back Up Project saves the whole project as one zip file. Restoring always goes into a new folder, so it never overwrites anything.
- **Upgrades.** When you open a project made with an older version, it's upgraded automatically, after a safety copy of its database is saved in the project's backups folder.
- **Integrity check.** It looks over the database, references, outlines, revision history, attribute values, and attachment hashes.

> **Important:** Once this version upgrades a project (database schema 10), older versions of TraceForge can't open it anymore. Keep a backup if you still need the older version.

## Requirements

### To run a published copy

- 64-bit Windows
- Nothing else, as long as the copy was published as self-contained

### To build or modify the project

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio, Visual Studio Code, or any editor that works with .NET

## Building the Project

Clone or download the repository, then run these from the project folder:

```powershell
dotnet build                          # debug build
dotnet run                            # start the desktop app
dotnet test TraceForge.sln            # run the automated tests
dotnet build TraceForge.sln -c Release
```

A debug build is only meant for development. To make a copy you can give to someone else, use `dotnet publish` as shown above.

## Project Structure

The desktop app sits at the solution root, and the libraries and tests each have their own folder.

| Path | Contents |
|---|---|
| `src/TraceForge.Domain` | The core logic, with no file or database access: outlines, import planning, markup, coverage, tracing, snapshots, reports, ReqIF |
| `src/TraceForge.Application` | The contracts for the project store and package service, plus the undo stack |
| `src/TraceForge.Infrastructure` | The SQLite store (one partial-class file per area), the package service, and the diagnostic log |
| Solution root | The WPF app: `MainWindow*.cs`, `Workspace.cs`, `Dialogs.cs`, `HelpGuide.cs`, and `Assets/` (the icon) |
| `tests/` | xUnit tests for the domain and infrastructure layers |
| `docs/PROJECT_FORMAT.md` | The project folder format, schema history, and lifecycle rules |

A project is a `.traceproj` folder containing `project.json`, `project.db` (SQLite), and an `attachments/` folder.

Outside of a project, TraceForge only writes to `%LocalAppData%\TraceForge\`: `ui.json` (your View menu choices), `diagnostics.log`, and `crash.log`. Help, Open Log Folder takes you there.

### Design Rules

If you're changing the code, these are worth keeping in mind:

- Siblings are ordered by `sort_order`, then name (ignoring case), then id. The store and the app have to agree on this.
- Two live siblings can't share a name, ignoring case.
- Audit events, revisions, baselines, and comments are append-only everywhere except when a module is permanently deleted.
- To change the database, add a migration and bump `SqliteProjectStore.LatestSchemaVersion` and `ProjectManifest.CurrentSchemaVersion` together. A test makes sure they match.
- Update `HelpGuide.cs` and `docs/PROJECT_FORMAT.md` whenever a feature or the format changes.

## Known Limitations

- Delete Permanently can't be undone, and it clears the undo history. Undo history only lasts for the current session.
- A deleted module stays live on purpose, so its objects still show up in global lists, search, reports, and link pickers until it's permanently deleted.
- Permanent delete leaves the module's attachment files behind until you run File, Clean Up Unused Attachment Files.
- Zipped `.reqifz` files and ReqIF relations can't be imported yet.

## Current Status

**Overall:** Release candidate (version 1.0.0-rc.1)

| Component | Status |
|---|---|
| Modules, folders, and outlines | Working |
| Links, suspect links, and impact analysis | Working |
| Revisions and baselines | Working |
| Comments and attachments | Working |
| CSV, ReqIF, and HTML import/export | Working |
| Backup, restore, and integrity check | Working |
| Built-in user guide | Working |
| Automated tests | Passing |

## Future Work

Right now the focus is on using it with real projects and fixing whatever turns up.

After that, improvements will come as new needs show up, and the project format will stay stable so existing projects keep opening.

## References

- [.NET — Microsoft](https://dotnet.microsoft.com/)
- [Windows Presentation Foundation — Microsoft](https://learn.microsoft.com/dotnet/desktop/wpf/)
- [SQLite](https://www.sqlite.org/)
- [ReqIF — Object Management Group](https://www.omg.org/spec/ReqIF/)

## License

Shield: [![CC BY-NC-SA 4.0][cc-by-nc-sa-shield]][cc-by-nc-sa]

This work is licensed under a
[Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License][cc-by-nc-sa].

[![CC BY-NC-SA 4.0][cc-by-nc-sa-image]][cc-by-nc-sa]

[cc-by-nc-sa]: http://creativecommons.org/licenses/by-nc-sa/4.0/
[cc-by-nc-sa-image]: https://licensebuttons.net/l/by-nc-sa/4.0/88x31.png
[cc-by-nc-sa-shield]: https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg
