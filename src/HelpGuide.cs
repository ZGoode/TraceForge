using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TraceForge.Domain;

namespace TraceForge.Desktop;

/// <summary>The built-in user guide: a topic list beside the selected topic. Topic text uses the same lightweight markup as object text.</summary>
internal static class HelpGuide
{
    private sealed record Topic(string Title, string Body);

    /// <summary>Title of the keyboard shortcuts topic, so the Help menu can open the guide there.</summary>
    public const string ShortcutsTopic = "Keyboard shortcuts";

    /// <summary>Builds the guide window. <paramref name="startTopic"/> is a topic title to show first (default: the first topic).</summary>
    public static Window Create(Window owner, string? startTopic = null)
    {
        var search = new TextBox { Margin = new Thickness(8, 0, 8, 8), ToolTip = "Type a word to list only the topics that mention it (Ctrl+F)" };
        AutomationProperties.SetName(search, "Search the guide");
        var searchLabel = new TextBlock { Text = "Search the guide", Margin = new Thickness(10, 8, 8, 2), Opacity = 0.75 };
        var list = new ListBox { BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(list, "Guide topics");
        var side = new DockPanel { Width = 240 };
        DockPanel.SetDock(searchLabel, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        side.Children.Add(searchLabel);
        side.Children.Add(search);
        side.Children.Add(list);
        var sideBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = System.Windows.Media.Brushes.LightGray, Child = side };

        var heading = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var content = new StackPanel();
        var page = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        page.Children.Add(heading);
        page.Children.Add(content);
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
        AutomationProperties.SetName(scroller, "Guide text");
        var noEmbeds = new Dictionary<string, EmbedData>();

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not string title) return;
            var topic = Topics.First(t => t.Title == title);
            heading.Text = topic.Title;
            MainWindow.RenderMarkup(content, topic.Body, noEmbeds);
            scroller.ScrollToTop();
        };

        // Lists the topics whose title or text contains the search words, keeping the current topic selected when it still matches.
        void FillList()
        {
            var query = search.Text.Trim();
            var keep = list.SelectedItem as string;
            var shown = Topics.Where(t => query.Length == 0 || t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
                              .Select(t => t.Title).ToList();
            list.ItemsSource = shown;
            if (shown.Count == 0)
            {
                heading.Text = "No topics found";
                content.Children.Clear();
                content.Children.Add(new TextBlock { Text = $"No topic mentions \"{query}\". Try a shorter or different word.", TextWrapping = TextWrapping.Wrap });
                return;
            }
            list.SelectedItem = keep is not null && shown.Contains(keep) ? keep : shown[0];
        }
        search.TextChanged += (_, _) => FillList();

        var layout = new DockPanel();
        DockPanel.SetDock(sideBorder, Dock.Left);
        layout.Children.Add(sideBorder);
        layout.Children.Add(scroller);
        var window = new Window
        {
            Title = "TraceForge User Guide", Owner = owner, Icon = owner.Icon, Content = layout, Width = 940, Height = 660, FontSize = 14,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Down && search.IsKeyboardFocused && list.Items.Count > 0) { list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                // First Escape clears the search, the next one closes the guide.
                if (search.Text.Length > 0) search.Clear(); else window.Close();
                e.Handled = true;
            }
        };
        window.Loaded += (_, _) =>
        {
            FillList();
            if (startTopic is not null && Topics.Any(t => t.Title == startTopic)) list.SelectedItem = startTopic;
            list.Focus();
        };
        return window;
    }

    private static readonly Topic[] Topics =
    [
        new("Getting started", """
            TraceForge keeps everything for one project in a single folder ending in **.traceproj**. The data stays on your computer, and every change is saved as soon as you make it. There is no Save button.

            **First steps**
            - File, New Project… asks you to choose a parent folder and a project name, then creates the project folder there.
            - Module, New Module (top level)… creates your first module, for example Stakeholder Needs.
            - Click **+ Object** to add a requirement, or **+ Heading** to add a section heading. Type the text and click OK.
            - To add the next level, select a module and use Module, New Child Module of Selected… (for example System Requirements under Stakeholder Needs). Then link objects from one level to the next (see Links and traceability).
            - File, Open Project… opens an existing project folder. Ctrl+N and Ctrl+O are the shortcuts.
            - Help, User Guide (F1) opens this guide at any time. Type in the search box above the topic list to find every topic that mentions a word.

            **Good to know**
            - Keep projects outside the folder TraceForge is installed in.
            - Ctrl+Z undoes your recent changes for as long as the project stays open, including renaming, moving, deleting and restoring modules and folders. Delete Permanently is the one exception (see Modules, folders and levels).
            - Nothing you delete is destroyed until you choose Delete Permanently; see Objects and outline and Modules, folders and levels.
            - Make a backup before big changes: File, Back Up Project… (see Safety, backups and files).
            """),
        new("The main window", """
            **Left: project modules.** Folders (bold) and modules. A module nested under another module is the next document level down. Right-click for the module menu.

            **Centre: the module table.** One row per object in outline order. The columns are:
            - **!** flags: ⚠ means a required attribute has no value, ⚑ means one of the object's links is suspect.
            - **◀** and **▶**: link arrows (see Links and traceability).
            - **Number** (outline number such as 1.2.3), **Object text**, **ID**.
            - **Linked from** and **Links to**: the IDs at the other end of each link.
            - One column per attribute of the module, then a **Notes** column (💬 open comment threads, 📎 attachments).

            **Top: the menus and toolbar.** The toolbar groups undo/redo, add, edit/copy/paste/delete, links and Go to, and arrange. Paste, Link and Arrange are drop-down buttons.

            **Bottom: the detail panel** for the selected object, with the tabs Trace, Links, History, Text preview, Comments and Attachments.

            **Right-click** almost anywhere (objects, column headers, modules, and the lists in the detail panel) for a menu of what you can do there.

            **View menu.** Show or hide the link ID columns, the notes column and the detail panel; compact rows; larger text. These choices are remembered between runs. Show all columns brings back anything you hid.
            """),
        new("Modules, folders and levels", """
            - A **module** is one document: a table of objects with its own attributes.
            - A **folder** only groups modules in the tree. It is not a level.
            - A module created under another module is its **child module**, the next level down. A chain such as Stakeholder Needs → Requirements → Design → Tests gives you four levels.

            Create them from the Module menu or by right-clicking in the tree:
            - New Module (top level)…
            - New Child Module of Selected…
            - New Folder (top level)… and New Subfolder of Selected…

            Select the parent first when you create a child module (a folder can be the parent too, so you can create a module inside a folder). Module and folder names must be different from their neighbours at the same level (not case-sensitive); the same name at different levels is fine. Object IDs are always unique across the project.

            **Arranging the tree.** Select a module or folder, then use the Module menu or right-click: Rename (F2), Move Up / Move Down (Alt+Up, Alt+Down), and Move To… (under another folder or module, or top level). New modules go last among their siblings; the order you set is kept. A module's child modules move with it. A module that has links coming from its parent level cannot be moved under a different parent until those links are removed, because links must always run from a module to its direct child module.

            **Deleting modules and folders.** Delete (Del in the tree) only marks a module or folder, and everything inside it, as deleted. Nothing is destroyed. Turn on View, Show deleted modules and folders to see them in light red with "(deleted)" after the name. A deleted module stays fully editable and its objects and links keep working everywhere, so you can repair links and content in it while the red marker reminds you it is on its way out. It no longer appears as a target in move lists or new-module parents. Restore brings it back (last among its siblings) together with everything deleted with it; the parent must be restored first and the name must not be taken. Delete and Restore can also be undone with Ctrl+Z, and an undone delete returns the item to its old place.\n\n            **Before deleting permanently.** Module, Show Links Into Deleted Modules… lists the links that connect deleted modules with modules that stay (select one deleted module first to see only its links). Open the modules, then repair or remove those links: they are destroyed along with the module. Delete Permanently (only for items already deleted, you must type the name) destroys the module, its objects, links, comments and revision history. The confirmation warns when links to modules that stay would be lost. It cannot be undone, and it clears the Undo history. It is refused when the module has a baseline, because baselines are frozen. Make a backup first.

            **Drag and drop in the tree.** Drag a module or folder onto another entry: the upper third of the entry puts it before, the lower third after, and the middle inside it (a folder cannot go inside a module). Drop on empty space in the tree to move it to the top level. The same rules as Move To… apply; when a drop is not allowed, a message says why and nothing changes.

            **Setting up a new module.** Attributes, Copy All Attribute Columns from Another Module… gives it the same columns as an existing module in one step. Attributes, Remove Attribute Column from This Module… takes a column away again once no object of the module has a value in it.
            """),
        new("Objects and outline", """
            **Adding.** Select a row, then use **+ Object** or **+ Heading** to add below it, or **+ Child** to add inside it. With nothing selected the new object goes at the end of the top level. IDs are made from the module name, for example REQUIR-003, and are never reused.

            **Editing text.** Select an object and press Ctrl+E (or Edit text…). The box accepts several lines. Formatting is lightweight and stored as plain text:
            - **bold** is written with two stars on each side, and italic with one star on each side.
            - Lines that start with "- " become bullets.
            - A backslash before a star shows the star itself.
            - Pictures and tables can be embedded (see Comments, attachments and pictures).

            **Arranging.** Use the Arrange button or the Module menu: Move Up (Alt+Up), Move Down (Alt+Down), Indent (Ctrl+Right), Outdent (Ctrl+Left). Indenting makes the object a child of the one above it. Outline numbers follow automatically.

            **Drag and drop.** Drag an object row to rearrange the outline: drop on the upper third of a row to place it before that row, the lower third for after it, and the middle to make it a child. The table must show outline order, so clear any sort or filter first (Clear). Ctrl+Z undoes a dragged move.

            **Copy and paste.** Copy (Ctrl+C) takes the selected object and everything below it. Paste (Ctrl+V) puts it below the selection and Paste as child (Ctrl+Shift+V) puts it inside it, in the same or another module. Pasted objects get new IDs; attribute values are kept where the target module uses the same attribute; links are not copied.

            **Delete and restore.** Delete (Del) removes the object and its whole section after you confirm. Nothing is destroyed: Module, Deleted Objects (Restore)… brings a section back where it was. Links of deleted objects stay hidden until you restore them.
            """),
        new("Attributes, filters and views", """
            **Attributes** add columns such as Priority or Status.
            - Attributes are defined once for the project, and each module chooses which ones it uses: Attributes, New Attribute for This Module… or Add Existing Attribute to This Module….
            - Types: text, whole number, real number, true/false, date (written yyyy-MM-dd) and enumeration (a fixed list of values).
            - A **required** attribute shows a star in its column heading and a ⚠ flag on rows that have no value. Required values can never be cleared.
            - Edit a value by typing in its cell. Invalid entries (for example text in a whole-number column) are rejected with a message.

            **Filtering.** The filter bar searches IDs, numbers, text and attribute values. **Filters** adds a filter on one attribute and on link status (no outgoing links, no incoming links, missing required values). **Clear** removes the filters and the sort.

            **Saved views.** The Views menu saves the current filter and the shown columns for this module under a name. Pick a view from the View box to apply it again. Saving under an existing name replaces it.

            **Sorting.** Click a column heading to sort ascending, again for descending, and a third time to go back to outline order. IDs and numbers sort naturally (R-2 before R-10, 1.2 before 1.10) and empty values stay last. Sorting only changes what you see, never the outline.

            **Columns.** Right-click a heading to hide the column, or use View, Choose shown attribute columns….
            """),
        new("Links and traceability", """
            **The rule.** A link goes from an object in a module to an object in one of its **direct child modules**: never upward, sideways, or skipping a level. TraceForge enforces this, so a link that breaks the rule is refused.

            **Creating links**
            - Select the parent object and choose Links, Link Selected to Child Objects… (Ctrl+L). Tick one or more objects in the child module, choose a link type (the default is Decomposes) and click OK.
            - Or select the child object and use Link Selected to Parent Objects… (Ctrl+Shift+L).
            - The Links tab lists every link of the selected object and lets you jump to the other end or remove the link.

            **Link arrows.** The **◀** (orange) column means other objects link to this one, from the level above. The **▶** (blue) column means this object links to the level below. A number shows how many links there are. Click an arrow to open a small list of the objects at the other end, then choose one to jump to it.

            **Finding things**
            - Go to Object… (Ctrl+G) searches IDs, text, module names and attribute values (for example Priority=high).
            - The **Trace** tab shows everything below the selected object across all levels. Links, Trace Whole Module shows every top-level section of the module.

            **Suspect links.** Every link remembers the revisions of both ends. When either object is edited later, the link becomes suspect and the object shows ⚑. Links, Suspect Links in This Module… lists them with the reason; tick the ones you have checked and click OK to mark them reviewed.

            **Impact.** Links, Impact of Selected Object shows everything reachable over links: downstream (what may need rework if this object changes) or upstream (what it derives from), with the depth.

            **Coverage.** Coverage Summary shows, for the module and its child levels, how many objects have links down to the next level and up from the level above. Traceability Gaps lists the objects missing an expected link, and Export Traceability Gaps (CSV) saves that list.
            """),
        new("History, baselines and undo", """
            **History.** Every change to an object's text or an attribute value adds an immutable revision. The History tab lists them newest first. Select an older revision and click **Restore this revision** to make the object equal to it. That creates a new revision, so history is never rewritten.

            **Baselines** freeze a module at one moment: objects, values, outline numbers and links. They can never be edited or deleted.
            - History, Create Baseline of This Module… freezes the current module under a name.
            - Create Baseline of Module and Child Modules… freezes the module and every module below it under one name, in one step.
            - View a Baseline… shows a frozen module on its own.
            - Compare Baseline with Current State…, Compare Two Baselines… and Compare Baseline Set with Current State… list added, removed, changed and renumbered objects and added or removed links.

            **Undo and redo** (Ctrl+Z and Ctrl+Y) cover adding, editing text and values, moving, deleting, restoring, copy/paste, adding and removing links, restoring a revision, whole imports, and renaming, moving, deleting and restoring modules and folders, for up to 100 steps while the project is open. Creating attributes, saved views and baselines cannot be undone, and neither can Delete Permanently, which also clears the Undo history.
            """),
        new("Comments, attachments and pictures", """
            **Comments.** Use the Comments tab: New comment…, Reply… (select a comment first) and Resolve / reopen thread. Comments are permanent and carry your Windows user name. Objects with open threads show 💬 and a count in the Notes column.

            **Attachments.** The Attachments tab copies a file into the project (limit 50 MB per file) and links it to the object. 📎 and a count appear in the Notes column.
            - **Open** works on a temporary copy; program and script types such as .exe, .bat and .ps1 are never launched. Use **Save copy…** for those.
            - **Remove** hides the attachment but keeps the file in the project.
            - Select a comment first and use **Attach file to comment…** to attach a file to that comment.

            **Pictures and tables in text.** Select an attachment, click **Copy embed code**, then paste the line on its own line in the object's text (Ctrl+E):
            - A picture (png, jpg, gif, bmp, webp) is written as an exclamation mark, the caption in square brackets, then attach: and the file name in round brackets.
            - A CSV or TSV file shown the same way appears as a table.
            - You can also type a table directly with pipes:

            | Mode | Limit |
            |------|-------|
            | Run | 21 C |

            Pictures and tables show in the Text preview tab, in comments and in the HTML report. Nothing is ever fetched from the internet, and SVG pictures are not shown.
            """),
        new("Import and export", """
            **CSV export.** File, Export This Module to CSV… writes Number, ID, Heading, Text, one column per attribute, Links to and Linked from (UTF-8 with a byte-order mark, so Excel opens it correctly). Export All Modules to a Folder… writes one file per module.

            **CSV import.** File, Import CSV into This Module… reads the same layout (comma, semicolon or tab separated; save as CSV UTF-8 from Excel). A preview shows what would be created, updated, left unchanged or skipped, plus every problem, before anything is written.
            - A row whose ID already exists in the module updates that object (text and changed values only; blank cells never clear a value; position does not change).
            - New IDs, or no ID, create objects. IDs are generated from the module name when blank.
            - A Number column (1, 1.1, 1.2.3) rebuilds the outline of new objects. Without it, new objects go to the top level.
            - Columns named like an attribute fill that attribute. Tick the box in the preview to create other columns as new Text attributes.
            - A Links to column such as Satisfies:SDD-001; SDD-002 creates links to objects in a direct child module. Unknown or wrong-level targets are reported and skipped.
            - The whole import is one transaction and one Undo step.

            **What an import changes on rows that already exist (matched by ID).** The text and attribute values from the file are applied. A blank cell never clears an existing value, and the Heading column and the Number column only matter for NEW rows: an existing object keeps its heading status and its place in the outline. To change those, edit in the table.

            **ReqIF.** Export Module and Children as ReqIF… writes one specification per module, with its outline, objects, attributes and links between the exported modules. Import ReqIF into This Module… reads a ReqIF file and hands it to the same preview as CSV. Relations in the file are not imported, and zipped .reqifz files are not supported yet: unzip them first.

            **HTML report.** Export Module and Children as HTML Report… writes one self-contained file with outline numbers, attributes, links, suspect marks, comments, pictures and tables. Open it in a browser and print or save as PDF from there.
            """),
        new("Safety, backups and files", """
            **Integrity check.** File, Check Integrity inspects the project: the database, references between records, outlines, revision history, attribute values and the hashes of attachments. Problems make a project invalid; warnings are reported but do not stop you. Warnings include leftovers from an interrupted write and, in old projects, two modules or folders with the same name at one level (rename all but one; the project works as it is, but you cannot create, rename or move into such a clash).

            **Audit log.** File, Show Audit Log… lists the newest 1000 entries of the project's append-only log: who did what, and when. Entries can never be edited or deleted.

            **Backups.** File, Back Up Project… saves the whole project (database and attachments) as one zip file. It is safe while the project is open and includes your latest edits; an existing backup file is never overwritten. File, Restore Project from Backup… unpacks a backup into a NEW project folder and opens it; nothing existing is overwritten. Back up before Delete Permanently, before large imports and before updating TraceForge. A project is also an ordinary folder, so copying the whole .traceproj folder while the project is closed works too.

            **Upgrades.** When a project from an older TraceForge is opened, it is upgraded automatically after a safety copy of the database is stored in the project's backups folder. A project from a newer TraceForge is refused rather than changed. An upgraded project can no longer be opened by the older version, so keep a backup if you still need it.

            **Permanent deletion.** Delete Permanently removes the module's records, but its attachment files stay in the project's attachments folder, and the audit log keeps its entries. File, Clean Up Unused Attachment Files… finds the files that no attachment uses any more (and leftovers of interrupted attachments) and deletes them after you confirm; make a backup first. Attachments you removed in the Attachments tab are not touched: they stay for the history.

            **Files outside projects.** TraceForge keeps three small files in %LocalAppData%\TraceForge\: ui.json (your View menu choices), diagnostics.log (one JSON line per event, with your user folder name hidden) and crash.log (details of unexpected errors).
            """),
        new("Glossary", """
            - **Project:** a folder ending in .traceproj holding everything: the database, attachments and settings.
            - **Folder:** groups modules in the tree. It is not a level.
            - **Module:** one document, a table of objects with its own attributes.
            - **Level:** a module nested under another module. Links run from one level to the next.
            - **Object:** one row: a requirement, or a heading that groups the rows below it.
            - **Attribute:** an extra column such as Priority or Status.
            - **Link:** a traceability relationship from an object to an object in a direct child module.
            - **Suspect link:** a link whose parent or child changed after the link was made or last reviewed.
            - **Revision:** one saved version of an object's text and values. Revisions are never rewritten.
            - **Baseline:** a frozen copy of a module (or a module and its children) at one moment.
            - **Deleted (red):** a module or folder marked for removal but still intact and restorable.
            - **Delete Permanently:** destroys a module that is already deleted, with its objects, links, comments and history.
            """),
        new(ShortcutsTopic, """
            Shortcuts do not fire while you are typing in a text box. Copy, paste and delete apply when the table has the focus; F2, Del and Alt+Up/Down act on the tree when the tree has the focus.

            | Keys | Action |
            |------|--------|
            | Ctrl+N | New project |
            | Ctrl+O | Open project |
            | Ctrl+Z / Ctrl+Y | Undo / Redo |
            | Ctrl+E | Edit the selected object's text |
            | Ctrl+C | Copy the selected object and its section |
            | Ctrl+V | Paste below the selection |
            | Ctrl+Shift+V | Paste as child |
            | Del | Delete the selected object and its section (table), or the selected module or folder (tree) |
            | Alt+Up / Alt+Down | Move the object up / down (table), or the module or folder up / down (tree) |
            | F2 | Rename the selected module or folder (tree) |
            | Ctrl+Right / Ctrl+Left | Indent / Outdent |
            | Ctrl+L | Link the selected object to child objects |
            | Ctrl+Shift+L | Link the selected object to parent objects |
            | Ctrl+G | Go to any object |
            | Ctrl+F | Jump to the filter box |
            | F1 | Open this guide |
            | Ctrl+F (in the guide) | Search the guide |
            """),
        new("Tips and troubleshooting", """
            - **A shortcut does nothing.** You are probably typing in a box. Click the table (or the tree) first.
            - **A link is refused.** Links only go from a module to one of its direct child modules. Create the child module first, or check the levels.
            - **Accented characters look wrong in Excel.** Save as CSV UTF-8 before importing, and open exported files with Data, From Text/CSV.
            - **I deleted something.** Module, Deleted Objects (Restore)… brings back whole sections. Ctrl+Z also reverses an object delete. A deleted module or folder comes back with Restore (View, Show deleted modules and folders, then select it).
            - **A module is shown in light red.** It is deleted but intact. Restore it, or delete it permanently once you are sure.
            - **Rename, Move or New child module is refused on a red module.** Restore it first; these need a live module.
            - **Delete Permanently is unavailable or refused.** It is only for modules and folders that are already deleted (red), and a module with a baseline can never be deleted permanently.
            - **A name is refused.** Two modules or folders at the same level cannot share a name; upper and lower case count as the same.
            - **Undo does not offer a Delete Permanently.** By design. Permanent deletion cannot be undone and clears the Undo history; restore from a backup instead.
            - **Dragging a row does nothing.** The table must be in outline order: click Clear to remove any sort or filter.
            - **A project will not open.** The message starts with a tag such as [manifest] or [database] that says what is wrong. Do not edit the project's files by hand; restore from your own copy of the folder.
            - **Something went wrong unexpectedly.** Look at crash.log and diagnostics.log in %LocalAppData%\TraceForge\ and keep them when you ask for help.
            """),
    ];
}
