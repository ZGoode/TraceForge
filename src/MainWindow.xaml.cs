using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using TraceForge.Application;
using TraceForge.Domain;
using TraceForge.Infrastructure;
using Container = TraceForge.Domain.Container; // System.ComponentModel also defines Container

namespace TraceForge.Desktop;

public partial class MainWindow : Window
{
    private static string DiagnosticLogPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraceForge", "diagnostics.log");
    private readonly ProjectPackageService _service = new(new FileDiagnosticLog(DiagnosticLogPath), Environment.UserName);
    private readonly Dictionary<DataGridColumn, AttributeDefinition> _attributeColumns = new();
    private readonly int _staticColumns;
    private Workspace? _workspace;
    private Container? _module;
    private bool _loading;
    private int _traceVersion;
    private int _moduleCount;
    private const long MaxReqIfBytes = 100L * 1024 * 1024;
    private sealed record Choice(string Name, object? Value);
    private ModuleView? _view;
    private ObjectId? _filterModule;
    private bool _applying;
    private IReadOnlyList<string>? _shownIds;
    private List<ContainerNode> _nodes = [];
    private string? _sortKey;
    private bool _sortDescending;

    public MainWindow()
    {
        InitializeComponent();
        _staticColumns = ObjectGrid.Columns.Count;
        BuildHeaderMenu();
        LoadUi();
        WireDragDrop();
        _applying = true;
        FilterLinks.ItemsSource = new[]
        {
            new Choice("Any links", LinkFilter.Any), new Choice("No outgoing links", LinkFilter.NoOutLinks),
            new Choice("No incoming links", LinkFilter.NoInLinks), new Choice("Missing required values", LinkFilter.MissingRequired),
        };
        FilterLinks.SelectedIndex = 0;
        _applying = false;
        UpdateMenus();
    }

    // ---------- helpers ----------

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateMenus()
    {
        var open = _workspace is not null;
        MenuClose.IsEnabled = open; MenuCheck.IsEnabled = open; MenuCleanAttachments.IsEnabled = open; MenuBackup.IsEnabled = open; MenuAudit.IsEnabled = open; MenuModule.IsEnabled = open; MenuAttributes.IsEnabled = open && _module is not null; MenuLinks.IsEnabled = open && _module is not null; MenuHistory.IsEnabled = open && _module is not null;
        MenuEdit.IsEnabled = open; MenuImport.IsEnabled = open && _module is not null; MenuExport.IsEnabled = open && _module is not null; MenuExportAll.IsEnabled = open; MenuExportHtml.IsEnabled = open && _module is not null; MenuImportReqIf.IsEnabled = open && _module is not null; MenuExportReqIf.IsEnabled = open && _module is not null;
        Title = open ? $"TraceForge — {_workspace!.Name}" : "TraceForge";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "TraceForge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool NeedModule()
    {
        if (_workspace is null || _module is null) { SetStatus("Select or create a module first (Module, New Module)."); return false; }
        return true;
    }

    // ---------- project ----------

    private async void NewProject_Click(object sender, RoutedEventArgs e) => await RunAsync(NewProjectAsync);
    private async void OpenProject_Click(object sender, RoutedEventArgs e) => await RunAsync(OpenProjectAsync);
    private async void CloseProject_Click(object sender, RoutedEventArgs e) => await RunAsync(CloseProjectAsync);
    private async void CheckIntegrity_Click(object sender, RoutedEventArgs e) => await RunAsync(CheckIntegrityAsync);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async Task NewProjectAsync()
    {
        var parent = new OpenFolderDialog { Title = "Choose the folder that will contain the new project" };
        if (parent.ShowDialog(this) != true) return;
        var name = Dialogs.Prompt(this, "New project", "Project name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { SetStatus("The project name contains characters that are not allowed in a folder name."); return; }
        var path = System.IO.Path.Combine(parent.FolderName, name + ".traceproj");
        await _service.CreateAsync(path, name);
        await OpenWorkspaceAsync(path);
    }

    private async Task OpenProjectAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Open a .traceproj project folder" };
        if (dialog.ShowDialog(this) != true) return;
        await OpenWorkspaceAsync(dialog.FolderName);
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        await CloseProjectAsync();
        var store = await _service.OpenAsync(path);
        _workspace = new Workspace(store, path);
        await RefreshTreeAsync(null);
        UpdateMenus();
        SetStatus($"Opened {_workspace.Name}. " + (_moduleCount == 0 ? "Create a module with Module, New Module." : "Select a module."));
    }

    private async Task CloseProjectAsync()
    {
        if (_workspace is null) return;
        await _workspace.DisposeAsync();
        _workspace = null; _module = null;
        ModuleTree.ItemsSource = null; TraceTree.ItemsSource = null; LinksList.ItemsSource = null; HistoryList.ItemsSource = null; CommentsList.ItemsSource = null; AttachmentsList.ItemsSource = null; _view = null; _filterModule = null; ShowRows(null);
        UpdateMenus(); SetStatus("No project is open.");
    }

    private async void BackUp_Click(object sender, RoutedEventArgs e) => await RunAsync(BackUpAsync);
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) => await RunAsync(RestoreBackupAsync);

    private async Task BackUpAsync()
    {
        if (_workspace is null) return;
        var dialog = new SaveFileDialog { Title = "Back up this project", Filter = "TraceForge backup (*.zip)|*.zip", FileName = $"{_workspace.Name} backup {DateTime.Now:yyyy-MM-dd HHmm}.zip", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        if (System.IO.File.Exists(dialog.FileName)) { SetStatus("That backup file already exists. Choose a new file name; existing backups are never overwritten."); return; }
        SetStatus("Backing up\u2026");
        await _service.CreateBackupAsync(_workspace.Path, dialog.FileName);
        SetStatus($"Backed up {_workspace.Name} to {dialog.FileName}. The backup contains everything as of now, including attachments.");
    }

    private async Task RestoreBackupAsync()
    {
        var file = new OpenFileDialog { Title = "Choose a TraceForge backup", Filter = "TraceForge backup (*.zip)|*.zip" };
        if (file.ShowDialog(this) != true) return;
        var parent = new OpenFolderDialog { Title = "Choose the folder that will contain the restored project" };
        if (parent.ShowDialog(this) != true) return;
        var name = Dialogs.Prompt(this, "Restore backup", "Name for the restored project (a new folder; nothing existing is overwritten):", System.IO.Path.GetFileNameWithoutExtension(file.FileName));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { SetStatus("The project name contains characters that are not allowed in a folder name."); return; }
        var path = System.IO.Path.Combine(parent.FolderName, name + ".traceproj");
        await _service.RestoreBackupAsync(file.FileName, path);
        await OpenWorkspaceAsync(path);
    }

    private async Task CheckIntegrityAsync()
    {
        if (_workspace is null) return;
        var report = await _service.CheckAsync(_workspace.Path);
        var text = report.IsValid ? "Integrity check passed." : "Integrity check FAILED:\n" + string.Join("\n", report.Findings);
        if (report.Warnings is { Count: > 0 }) text += "\n\nWarnings:\n" + string.Join("\n", report.Warnings);
        SetStatus(report.IsValid ? "Integrity check passed." : $"Integrity check found {report.Findings.Count} problem(s).");
        MessageBox.Show(this, text, "Integrity check", MessageBoxButton.OK, report.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        DeleteTempCopies();
        if (_workspace is null) return;
        e.Cancel = true; // release the database handles first, then close for real
        var workspace = _workspace; _workspace = null;
        try { await workspace.DisposeAsync(); }
        catch (Exception) { /* a failed dispose must never stop the window from closing */ }
        Closing -= Window_Closing;
        // The dispose can finish synchronously, i.e. still inside this Closing event, where WPF forbids Close().
        // Queue the real close so it runs after the event has returned.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private readonly List<string> _tempFolders = new();

    /// <summary>Removes the temporary attachment copies made by Open; files still held open by another program are left for the OS temp cleanup.</summary>
    private void DeleteTempCopies()
    {
        foreach (var folder in _tempFolders)
        {
            try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
        _tempFolders.Clear();
    }

    private Window? _help;

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();
    private void HelpShortcuts_Click(object sender, RoutedEventArgs e) => ShowHelp(HelpGuide.ShortcutsTopic);
    private void About_Click(object sender, RoutedEventArgs e) => Dialogs.About(this);

    /// <summary>Opens the user guide, or brings it forward. With a topic title the guide jumps to that topic.</summary>
    private void ShowHelp(string? topic = null)
    {
        if (_help is { IsLoaded: true })
        {
            if (topic is null) { _help.Activate(); return; }
            _help.Close();
        }
        _help = HelpGuide.Create(this, topic);
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(DiagnosticLogPath)!;
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Could not open the log folder: " + ex.Message);
        }
    }

    // ---------- modules ----------

    private async Task RefreshTreeAsync(ObjectId? select)
    {
        if (_workspace is null) return;
        var roots = await _workspace.LoadTreeAsync(_ui.ShowDeleted);
        IEnumerable<ContainerNode> Flatten(IEnumerable<ContainerNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
        var flat = Flatten(roots).ToList();
        _nodes = flat;
        _moduleCount = flat.Count(n => n.Container.Kind == ContainerKind.Module);
        var chosen = select is { } id ? flat.FirstOrDefault(n => n.Container.Id == id) : null;
        chosen ??= flat.FirstOrDefault(n => n.Container.Kind == ContainerKind.Module);
        _loading = true;
        ModuleTree.ItemsSource = roots;
        if (chosen is not null) chosen.IsSelected = true;
        _loading = false;
        _module = chosen is not null && chosen.Container.Kind == ContainerKind.Module ? chosen.Container : null;
        await ReloadAsync(null);
    }

    private async void ModuleTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loading) return;
        var node = e.NewValue as ContainerNode;
        var module = node is not null && node.Container.Kind == ContainerKind.Module ? node.Container : null;
        if (module?.Id == _module?.Id) return;
        _module = module;
        await RunAsync(() => ReloadAsync(null));
        if (node is { IsDeleted: true }) SetStatus($"{node.Name} is deleted (shown red) but still fully editable: fix its links and content, then Delete Permanently, or Restore it.");
    }

    private async void NewModule_Click(object sender, RoutedEventArgs e) => await RunAsync(() => NewContainerAsync(ContainerKind.Module, false));
    private async void NewChildModule_Click(object sender, RoutedEventArgs e) => await RunAsync(() => NewContainerAsync(ContainerKind.Module, true));
    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await RunAsync(() => NewContainerAsync(ContainerKind.Folder, false));

    private async Task NewContainerAsync(ContainerKind kind, bool asChild)
    {
        if (_workspace is null) return;
        var parent = asChild ? ModuleTree.SelectedItem as ContainerNode : null;
        if (asChild && parent is null) { SetStatus("Select the parent module or folder in the tree first."); return; }
        var what = kind == ContainerKind.Module ? "module" : "folder";
        var name = Dialogs.Prompt(this, $"New {what}", asChild ? $"Name of the new {what} under {parent!.Name}:" : $"Name of the new {what}:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var created = await _workspace.CreateContainerAsync(name, kind, parent?.Container.Id);
        await RefreshTreeAsync(created.Id);
        SetStatus(kind == ContainerKind.Module ? $"Created module {created.Name}. Add objects with the toolbar (+ Heading / + Object)." : $"Created folder {created.Name}.");
    }

    private async void NewSubfolder_Click(object sender, RoutedEventArgs e) => await RunAsync(() => NewContainerAsync(ContainerKind.Folder, true));
    private async void RenameContainer_Click(object sender, RoutedEventArgs e) => await RunAsync(RenameContainerAsync);
    private async void ContainerUp_Click(object sender, RoutedEventArgs e) => await RunAsync(() => NudgeContainerAsync(-1));
    private async void ContainerDown_Click(object sender, RoutedEventArgs e) => await RunAsync(() => NudgeContainerAsync(1));
    private async void MoveContainerTo_Click(object sender, RoutedEventArgs e) => await RunAsync(MoveContainerToAsync);
    private async void DeleteContainer_Click(object sender, RoutedEventArgs e) => await RunAsync(DeleteContainerAsync);

    private ContainerNode? SelectedNode => ModuleTree.SelectedItem as ContainerNode;

    private bool NeedNode(out ContainerNode node)
    {
        node = SelectedNode!;
        if (_workspace is not null && node is not null) return true;
        SetStatus("Select a module or folder in the tree first.");
        return false;
    }

    private async Task RenameContainerAsync()
    {
        if (!NeedNode(out var node)) return;
        var name = Dialogs.Prompt(this, "Rename", $"New name for {node.Name}:", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name) return;
        await _workspace!.RenameContainerAsync(node.Container, name);
        await RefreshTreeAsync(node.Container.Id);
        SetStatus($"Renamed {node.Name} to {name.Trim()}. Existing object IDs keep their old prefix.");
    }

    private async Task NudgeContainerAsync(int delta)
    {
        if (!NeedNode(out var node)) return;
        if (!await _workspace!.NudgeContainerAsync(node.Container, delta)) { SetStatus($"{node.Name} is already the {(delta < 0 ? "first" : "last")} one at its level."); return; }
        await RefreshTreeAsync(node.Container.Id);
        SetStatus($"Moved {node.Name} {(delta < 0 ? "up" : "down")}.");
    }

    private async Task MoveContainerToAsync()
    {
        if (!NeedNode(out var node)) return;
        var banned = new HashSet<ObjectId>();
        void Collect(ContainerNode n) { banned.Add(n.Container.Id); foreach (var child in n.Children) Collect(child); }
        Collect(node);
        var byId = _nodes.ToDictionary(n => n.Container.Id);
        string PathOf(ContainerNode n)
        {
            var parts = new List<string>();
            for (ObjectId? cursor = n.Container.Id; cursor is { } id && byId.TryGetValue(id, out var x); cursor = x.Container.ParentId) parts.Insert(0, x.Name);
            return string.Join(" / ", parts);
        }
        var choices = new List<Choice> { new("(top level)", null) };
        choices.AddRange(_nodes.Where(n => !n.IsDeleted && !banned.Contains(n.Container.Id) && (node.Container.Kind == ContainerKind.Module || n.Container.Kind == ContainerKind.Folder))
            .Select(n => new Choice((n.Container.Kind == ContainerKind.Folder ? "[folder] " : "") + PathOf(n), n.Container.Id)));
        var pick = Dialogs.Choose(this, "Move", $"Move {node.Name} under:", choices, c => c.Name);
        if (pick is null) return;
        await _workspace!.MoveContainerAsync(node.Container, pick.Value is ObjectId target ? target : null);
        await RefreshTreeAsync(node.Container.Id);
        SetStatus($"Moved {node.Name} to {pick.Name}. A module's child modules move with it.");
    }

    private async void RestoreContainer_Click(object sender, RoutedEventArgs e) => await RunAsync(RestoreContainerAsync);
    private async void PurgeContainer_Click(object sender, RoutedEventArgs e) => await RunAsync(PurgeContainerAsync);

    private async Task DeleteContainerAsync()
    {
        if (!NeedNode(out var node)) return;
        if (node.IsDeleted) { SetStatus($"{node.Name} is already deleted. Use Restore, or Delete Permanently."); return; }
        var what = node.Container.Kind == ContainerKind.Folder ? "folder" : "module";
        var objects = await _workspace!.CountObjectsUnderAsync(node.Container);
        var inside = node.Children.Count > 0 || objects > 0 ? $"\n\nIt and everything inside it ({node.Children.Count} direct child item(s), {objects} object(s)) will be marked red when shown. Its content stays editable." : "";
        if (MessageBox.Show(this, $"Delete the {what} “{node.Name}”?{inside}\n\nNothing is destroyed. Turn on View, Show deleted modules and folders to see it again in light red; there you can restore it or delete it permanently.", "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await _workspace!.SoftDeleteContainerAsync(node.Container);
        await RefreshTreeAsync(_ui.ShowDeleted ? node.Container.Id : null);
        SetStatus(_ui.ShowDeleted ? $"Deleted {what} {node.Name}. It is shown in light red: Restore it, or Delete Permanently." : $"Deleted {what} {node.Name}. Turn on View, Show deleted modules and folders to restore it.");
    }

    private async Task RestoreContainerAsync()
    {
        if (!NeedNode(out var node)) return;
        if (!node.IsDeleted) { SetStatus($"{node.Name} is not deleted."); return; }
        await _workspace!.RestoreContainerAsync(node.Container);
        await RefreshTreeAsync(node.Container.Id);
        SetStatus($"Restored {node.Name}, with everything that was deleted together with it. It is now last among its siblings.");
    }

    private async Task PurgeContainerAsync()
    {
        if (!NeedNode(out var node)) return;
        if (!node.IsDeleted) { SetStatus("Only a deleted module or folder can be deleted permanently. Delete it first."); return; }
        var objects = await _workspace!.CountObjectsUnderAsync(node.Container);
        var crossing = await _workspace.LinksIntoDeletedAsync(node.Container);
        var warning = crossing.Count == 0 ? "" : $"WARNING: {crossing.Count} link(s) connect it with objects in modules that stay, for example {crossing[0].From}  \u2192  {crossing[0].To}. They will be destroyed too (Module, Show Links Into Deleted Modules lists them).\n\n";
        var typed = Dialogs.Prompt(this, "Delete permanently",
            $"This destroys “{node.Name}”, everything inside it ({objects} object(s)), their links, comments, revision history and attachment records. It cannot be undone, is not covered by Undo, and clears the undo history.\n\n{warning}A backup (File, Back Up Project…) first is a good idea.\n\nType the name to confirm:");
        if (typed is null) return;
        if (!string.Equals(typed.Trim(), node.Name, StringComparison.Ordinal)) { SetStatus("The name did not match, so nothing was deleted."); return; }
        await _workspace.PurgeContainerAsync(node.Container);
        await RefreshTreeAsync(null);
        SetStatus($"Permanently deleted {node.Name} ({objects} object(s)).");
    }

    private async void LinksIntoDeleted_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_workspace is null) return;
        var root = SelectedNode is { IsDeleted: true } deleted ? deleted.Container : null;
        var rows = await _workspace.LinksIntoDeletedAsync(root);
        var scope = root is null ? "any deleted module" : $"the deleted module {root.Name} (and what is below it)";
        if (rows.Count == 0) { SetStatus($"No links connect {scope} with modules that stay."); return; }
        Dialogs.ShowTable(this, "Links into deleted modules", $"{rows.Count} link(s) between {scope} and modules that stay. Deleted modules stay fully editable, so open them to repair or remove these links before deleting anything permanently.", rows,
            ("From", nameof(Workspace.DeletedLinkRow.From), 330), ("To", nameof(Workspace.DeletedLinkRow.To), 330), ("Type", nameof(Workspace.DeletedLinkRow.Type), 90), ("Direction", nameof(Workspace.DeletedLinkRow.Direction), 150));
    });

    private async void CleanAttachments_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_workspace is null) return;
        var referenced = await _workspace.Store.ListReferencedAttachmentIdsAsync();
        var unused = await _service.FindUnusedAttachmentsAsync(_workspace.Path, referenced);
        if (unused.Count == 0) { SetStatus("No unused attachment files: every file in the project's attachments folder is used."); return; }
        var bytes = unused.Sum(f => f.SizeBytes);
        var size = bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";
        var answer = MessageBox.Show(this,
            $"{unused.Count} file(s) ({size}) in this project's attachments folder are not used by any attachment. They are left over from Delete Permanently or from interrupted attachments.\n\nAttachments you removed in the Attachments tab are not affected; they stay for the history.\n\nDelete these files permanently? This cannot be undone. A backup (File, Back Up Project…) first is a good idea.",
            "Clean up attachment files", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        var removed = await _service.DeleteUnusedAttachmentsAsync(_workspace.Path, referenced);
        SetStatus($"Deleted {removed} unused attachment file(s) ({size}).");
    });

    private async void CopyAttributes_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var others = _nodes.Where(n => !n.IsDeleted && n.Container.Kind == ContainerKind.Module && n.Container.Id != _module!.Id).ToList();
        if (others.Count == 0) { SetStatus("There is no other module to copy attribute columns from."); return; }
        var source = Dialogs.Choose(this, "Copy attribute columns", $"Give {_module!.Name} the same attribute columns as:", others, n => n.Name);
        if (source is null) return;
        var added = await _workspace!.CopyAttributeColumnsAsync(_module, source.Container);
        await ReloadAsync((ObjectGrid.SelectedItem as RowViewModel)?.Id);
        SetStatus(added == 0 ? $"{_module.Name} already has every attribute column of {source.Name}." : $"Added {added} attribute column(s) from {source.Name} to {_module.Name}.");
    });

    private async void RemoveAttribute_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var assigned = await _workspace!.Store.ListModuleAttributesAsync(_module!.Id);
        if (assigned.Count == 0) { SetStatus("This module has no attribute columns."); return; }
        var attribute = Dialogs.Choose(this, "Remove attribute column", $"Attribute column to remove from {_module.Name}:", assigned, a => $"{a.Name} ({a.DataType})");
        if (attribute is null) return;
        await _workspace.Store.RemoveAttributeFromModuleAsync(_module.Id, attribute.Id);
        await ReloadAsync((ObjectGrid.SelectedItem as RowViewModel)?.Id);
        SetStatus($"Removed the column {attribute.Name} from {_module.Name}. The attribute itself stays available to other modules.");
    });

    /// <summary>Makes the tree node's module the current one without triggering the tree's selection handler.</summary>
    private void SelectNode(ContainerNode node)
    {
        _module = node.Container;
        _loading = true; node.IsSelected = true; _loading = false;
    }

    // ---------- table ----------

    private async Task ReloadAsync(ObjectId? reselect)
    {
        if (_workspace is null || _module is null)
        {
            _view = null; _filterModule = null; ShowRows(null); UpdateMenus(); await RefreshDetailsAsync(); return;
        }
        var view = await _workspace.LoadModuleAsync(_module);
        var moduleChanged = _filterModule != _module.Id;
        _filterModule = _module.Id;
        _view = view;
        if (moduleChanged) _sortKey = null;
        SetupFilterControls(view, moduleChanged);
        BuildColumns(view);
        ApplyFilter();
        if (reselect is { } id && view.Rows.FirstOrDefault(r => r.Id == id) is { } row && ObjectGrid.Items.Contains(row))
        {
            ObjectGrid.SelectedItem = row; ObjectGrid.ScrollIntoView(row);
        }
        UpdateMenus();
        await RefreshDetailsAsync();
    }

    private void ShowRows(ModuleView? view)
    {
        BuildColumns(view);
        ObjectGrid.ItemsSource = view?.Rows;
    }

    private void BuildColumns(ModuleView? view)
    {
        while (ObjectGrid.Columns.Count > _staticColumns) ObjectGrid.Columns.RemoveAt(ObjectGrid.Columns.Count - 1);
        _attributeColumns.Clear();
        if (view is null) return;
        foreach (var attribute in view.Attributes)
        {
            var column = new DataGridTextColumn
            {
                Header = attribute.IsRequired ? attribute.Name + " *" : attribute.Name,
                Binding = new Binding($"[{attribute.Id}]") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(140),
            };
            ObjectGrid.Columns.Add(column);
            _attributeColumns[column] = attribute;
        }
    }

    // ---------- filters and saved views ----------

    private void SetupFilterControls(ModuleView view, bool reset)
    {
        _applying = true;
        var attrId = reset ? null : ((FilterAttr.SelectedItem as Choice)?.Value as AttributeDefinition)?.Id;
        var viewName = reset ? null : ((ViewBox.SelectedItem as Choice)?.Value as SavedView)?.Name;
        var attrs = new List<Choice> { new("(any attribute)", null) };
        attrs.AddRange(view.Attributes.Select(a => new Choice(a.Name, a)));
        FilterAttr.ItemsSource = attrs;
        FilterAttr.SelectedIndex = Math.Max(0, attrs.FindIndex(c => c.Value is AttributeDefinition d && d.Id == attrId));
        var views = new List<Choice> { new("(no saved view)", null) };
        views.AddRange(view.Views.Select(v => new Choice(v.Name, v)));
        ViewBox.ItemsSource = views;
        ViewBox.SelectedIndex = Math.Max(0, views.FindIndex(c => c.Value is SavedView v && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase)));
        if (reset) { FilterText.Text = ""; FilterAttrText.Text = ""; FilterLinks.SelectedIndex = 0; _shownIds = null; }
        _applying = false;
    }

    private ViewDefinition BuildDefinition() => new(
        FilterText.Text, ((FilterAttr.SelectedItem as Choice)?.Value as AttributeDefinition)?.Id.ToString(), FilterAttrText.Text,
        (FilterLinks.SelectedItem as Choice)?.Value is LinkFilter lf ? lf : LinkFilter.Any, _shownIds);

    private void ApplyFilter()
    {
        if (_view is null) return;
        var definition = BuildDefinition();
        var previous = (ObjectGrid.SelectedItem as RowViewModel)?.Id;
        var rows = SortRows(_view.Rows.Where(r => ViewFilter.Matches(definition, r.Facts)).ToList());
        ObjectGrid.ItemsSource = rows;
        UpdateSortIndicators();
        if (previous is { } id && rows.FirstOrDefault(r => r.Id == id) is { } keep) ObjectGrid.SelectedItem = keep;
        foreach (var (column, attribute) in _attributeColumns)
            column.Visibility = _shownIds is null || _shownIds.Contains(attribute.Id.ToString()) ? Visibility.Visible : Visibility.Collapsed;
        var missing = _view.Rows.Count(r => r.MissingText is not null);
        SetStatus($"{_module?.Name}: " + (definition.IsFiltering ? $"showing {rows.Count} of {_view.Rows.Count} object(s)" : $"{rows.Count} object(s)") +
                  (missing > 0 ? $", {missing} with missing required values" : "") + (_sortKey is null ? "." : $", sorted ({(_sortDescending ? "descending" : "ascending")}); Clear restores outline order."));
    }

    // ---------- sorting (view only: the outline order itself is never changed) ----------

    private string? KeyOf(DataGridColumn column)
    {
        var index = ObjectGrid.Columns.IndexOf(column);
        if (index < 0) return null;
        return index < _staticColumns ? $"s{index}" : _attributeColumns.TryGetValue(column, out var a) ? a.Id.ToString() : null;
    }

    private sealed class SortCompare(bool numeric, bool descending) : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            if (x.Length == 0 || y.Length == 0) return x.Length == y.Length ? 0 : x.Length == 0 ? 1 : -1; // empty values sort last either way
            int c;
            if (numeric && double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
                        && double.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)) c = a.CompareTo(b);
            else c = NaturalOrder.Instance.Compare(x, y);
            return descending ? -c : c;
        }
    }

    private List<RowViewModel> SortRows(List<RowViewModel> rows)
    {
        if (_sortKey is null) return rows;
        Func<RowViewModel, string>? key = _sortKey switch
        {
            "s0" => r => r.Flag, "s1" => r => r.InLinks.ToString("D5"), "s2" => r => r.OutLinks.ToString("D5"), "s3" => r => r.Number, "s4" => r => r.Title, "s5" => r => r.CanonicalId,
            "s6" => r => r.InSummary, "s7" => r => r.OutSummary, "s8" => r => r.Notes, _ => null,
        };
        var numeric = false;
        if (key is null)
        {
            var attribute = _view?.Attributes.FirstOrDefault(a => a.Id.ToString() == _sortKey);
            if (attribute is null) { _sortKey = null; return rows; }
            var id = attribute.Id.ToString();
            numeric = attribute.DataType is AttributeDataType.Integer or AttributeDataType.Real;
            key = r => r[id];
        }
        return rows.OrderBy(key, new SortCompare(numeric, _sortDescending)).ToList();
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in ObjectGrid.Columns)
            column.SortDirection = KeyOf(column) is { } k && k == _sortKey ? (_sortDescending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending) : null;
    }

    private void ObjectGrid_Sorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // we sort ourselves so numbers and IDs sort naturally and the outline order stays the base order
        var key = KeyOf(e.Column);
        if (key is null) return;
        if (_sortKey != key) { _sortKey = key; _sortDescending = false; }
        else if (!_sortDescending) _sortDescending = true;
        else _sortKey = null; // third click: back to outline order
        ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) { if (!_applying && _view is not null) ApplyFilter(); }

    private void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying || _view is null || (ViewBox.SelectedItem as Choice)?.Value is not SavedView saved) return;
        var d = saved.Definition;
        _applying = true;
        FilterText.Text = d.Text; FilterAttrText.Text = d.AttributeText;
        var attrs = FilterAttr.Items.OfType<Choice>().ToList();
        FilterAttr.SelectedIndex = Math.Max(0, attrs.FindIndex(c => c.Value is AttributeDefinition a && a.Id.ToString() == d.AttributeId));
        var links = FilterLinks.Items.OfType<Choice>().ToList();
        FilterLinks.SelectedIndex = Math.Max(0, links.FindIndex(c => c.Value is LinkFilter f && f == d.LinkFilter));
        _shownIds = d.ShownAttributeIds;
        _applying = false;
        ApplyFilter();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e) { ClearFilterControls(); _sortKey = null; ApplyFilter(); }

    private void ClearFilterControls()
    {
        _applying = true;
        FilterText.Text = ""; FilterAttrText.Text = "";
        if (FilterAttr.Items.Count > 0) FilterAttr.SelectedIndex = 0;
        if (FilterLinks.Items.Count > 0) FilterLinks.SelectedIndex = 0;
        if (ViewBox.Items.Count > 0) ViewBox.SelectedIndex = 0;
        _shownIds = null;
        _applying = false;
    }

    private async void SaveView_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule() || _view is null) return;
        var current = ((ViewBox.SelectedItem as Choice)?.Value as SavedView)?.Name ?? "";
        var name = Dialogs.Prompt(this, "Save view", "Name for this view (an existing view with the same name is replaced):", current);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _workspace!.Store.SaveViewAsync(new SavedView(ObjectId.New(), _module!.Id, name, BuildDefinition(), DateTimeOffset.UtcNow));
        await ReloadAsync((ObjectGrid.SelectedItem as RowViewModel)?.Id);
        _applying = true;
        var items = ViewBox.Items.OfType<Choice>().ToList();
        ViewBox.SelectedIndex = Math.Max(0, items.FindIndex(c => c.Value is SavedView v && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)));
        _applying = false;
        SetStatus($"Saved view \u201C{name}\u201D.");
    });

    private async void DeleteView_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if ((ViewBox.SelectedItem as Choice)?.Value is not SavedView saved) { SetStatus("Pick a saved view to delete first."); return; }
        if (MessageBox.Show(this, $"Delete the saved view \u201C{saved.Name}\u201D?", "Delete view", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await _workspace!.Store.DeleteViewAsync(saved.Id);
        ClearFilterControls();
        await ReloadAsync((ObjectGrid.SelectedItem as RowViewModel)?.Id);
        SetStatus($"Deleted view \u201C{saved.Name}\u201D.");
    });

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null || _view.Attributes.Count == 0) { SetStatus("This module has no attribute columns yet (Attributes menu)."); return; }
        var items = _view.Attributes.Select(a => new Dialogs.PickItem(a, a.Name, _shownIds is null || _shownIds.Contains(a.Id.ToString()))).ToList();
        var result = Dialogs.PickMany(this, "Shown columns", "Attribute columns to show:", items, allowEmpty: true);
        if (result is null) return;
        var ids = result.Value.Picked.Select(p => ((AttributeDefinition)p.Tag).Id.ToString()).ToList();
        _shownIds = ids.Count == _view.Attributes.Count ? null : ids;
        ApplyFilter();
    }

    // ---------- details: trace and links ----------

    private async void ObjectGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(RefreshDetailsAsync);
    private async void TraceWhole_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshDetailsAsync);

    private async Task RefreshDetailsAsync()
    {
        var version = ++_traceVersion;
        if (_workspace is null || _module is null)
        {
            TraceTree.ItemsSource = null; LinksList.ItemsSource = null; HistoryList.ItemsSource = null; CommentsList.ItemsSource = null; CommentPreview.Children.Clear(); AttachmentsList.ItemsSource = null; PreviewPanel.Children.Clear(); TraceHeader.Text = "Trace"; LinksHeader.Text = ""; HistoryHeader.Text = ""; CommentsHeader.Text = ""; AttachmentsHeader.Text = ""; return;
        }
        var row = ObjectGrid.SelectedItem as RowViewModel;
        var whole = MenuTraceWhole.IsChecked || row is null;
        var items = await _workspace.LoadTraceAsync(_module, whole ? null : row!.Id);
        var links = row is null ? new List<LinkItem>() : await _workspace.LoadLinksAsync(row.Id);
        var history = row is null ? new List<HistoryItem>() : await _workspace.LoadHistoryAsync(row.Id);
        var comments = row is null ? new List<Workspace.CommentItem>() : await _workspace.LoadCommentsAsync(row.Id);
        var attachments = row is null ? new List<Workspace.AttachmentItem>() : await _workspace.LoadAttachmentsAsync(row.Id);
        var embeds = row is null ? new Dictionary<string, EmbedData>() : await _workspace.LoadEmbedsAsync(row.Id, row.Title);
        if (version != _traceVersion) return; // a newer request superseded this one
        TraceTree.ItemsSource = items;
        TraceHeader.Text = (whole ? $"Trace \u2014 whole module {_module.Name}" : $"Trace \u2014 {row!.Number} {row.CanonicalId} and everything below it") + "   (double-click to jump)";
        LinksList.ItemsSource = links;
        LinksHeader.Text = row is null ? "Select an object." : $"{row.CanonicalId}: {links.Count} link(s)";
        HistoryList.ItemsSource = history;
        HistoryHeader.Text = row is null ? "Select an object." : $"{row.CanonicalId}: {history.Count} revision(s), newest first";
        ShowPreview(row?.Title, embeds);
        CommentsList.ItemsSource = comments;
        CommentsHeader.Text = row is null ? "Select an object." : $"{row.CanonicalId}: {comments.Count(c => !c.IsReply)} thread(s), {comments.Count(c => !c.IsReply && !c.Resolved)} open";
        AttachmentsList.ItemsSource = attachments;
        AttachmentsHeader.Text = row is null ? "Select an object." : $"{row.CanonicalId}: {attachments.Count} file(s)";
    }

    private static void AddRuns(System.Windows.Controls.TextBlock block, IEnumerable<MarkupRun> runs)
    {
        foreach (var r in runs)
        {
            var run = new System.Windows.Documents.Run(r.Text);
            if (r.Bold) run.FontWeight = FontWeights.Bold;
            if (r.Italic) run.FontStyle = FontStyles.Italic;
            block.Inlines.Add(run);
        }
    }

    private static FrameworkElement BuildTable(bool hasHeader, IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkupRun>>> rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        for (var c = 0; c < width; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 360 });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < rows[r].Count; c++)
            {
                var text = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2) };
                AddRuns(text, rows[r][c]);
                var header = hasHeader && r == 0;
                if (header) text.FontWeight = FontWeights.Bold;
                var cell = new Border { BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(0.5), Child = text, Background = header ? System.Windows.Media.Brushes.Gainsboro : System.Windows.Media.Brushes.Transparent };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    private void ShowPreview(string? text, IReadOnlyDictionary<string, EmbedData> embeds) => RenderMarkup(PreviewPanel, text, embeds);

    /// <summary>Draws formatted text, tables and embedded pictures into the panel (used for object text and for comments).</summary>
    internal static void RenderMarkup(Panel target, string? text, IReadOnlyDictionary<string, EmbedData> embeds)
    {
        target.Children.Clear();
        if (text is null) return;
        foreach (var block in Markup.ParseBlocks(text))
        {
            switch (block)
            {
                case ParagraphBlock p:
                {
                    var tb = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                    var first = true;
                    foreach (var line in p.Lines)
                    {
                        if (!first) tb.Inlines.Add(new System.Windows.Documents.LineBreak());
                        first = false;
                        if (line.IsBullet) tb.Inlines.Add(new System.Windows.Documents.Run("   \u2022 "));
                        AddRuns(tb, line.Runs);
                    }
                    target.Children.Add(tb);
                    break;
                }
                case TableBlock t:
                    target.Children.Add(BuildTable(t.HasHeader, t.Rows));
                    break;
                case EmbedBlock e:
                    target.Children.Add(BuildEmbed(e, embeds));
                    break;
            }
        }
    }

    private static UIElement BuildEmbed(EmbedBlock e, IReadOnlyDictionary<string, EmbedData> embeds)
    {
        var note = (string message) => new System.Windows.Controls.TextBlock { Text = message, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 4) };
        if (!embeds.TryGetValue(e.Name, out var data)) return note($"[attachment not found, or too large to show: {e.Name}]");
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        if (data.IsImage && data.Bytes is { } bytes)
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new System.IO.MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                panel.Children.Add(new System.Windows.Controls.Image { Source = bitmap, MaxWidth = 720, MaxHeight = 540, Stretch = System.Windows.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
            }
            catch (Exception) { return note($"[the picture {e.Name} could not be read]"); }
        }
        else if (data.Rows is { } rows)
            panel.Children.Add(BuildTable(true, rows.Select(r => (IReadOnlyList<IReadOnlyList<MarkupRun>>)r.Select(c => (IReadOnlyList<MarkupRun>)new[] { new MarkupRun(c, false, false) }).ToList()).ToList()));
        else return note($"[cannot show {e.Name}]");
        if (e.Alt.Length > 0) panel.Children.Add(new System.Windows.Controls.TextBlock { Text = e.Alt, Foreground = System.Windows.Media.Brushes.Gray });
        return panel;
    }

    // ---------- link arrows ----------

    private void InArrow_Click(object sender, MouseButtonEventArgs e) => ShowLinkPopup(sender, e, incoming: true);
    private void OutArrow_Click(object sender, MouseButtonEventArgs e) => ShowLinkPopup(sender, e, incoming: false);

    /// <summary>Clicking a link arrow opens a small menu under it listing the linked objects; choosing one jumps there (see <see cref="CmGo_Click"/>).</summary>
    private void ShowLinkPopup(object sender, MouseButtonEventArgs e, bool incoming)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: RowViewModel row } anchor) return;
        var refs = incoming ? row.InRefs : row.OutRefs;
        if (refs.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var r in refs)
        {
            // "_" would be read as a keyboard-accelerator marker in a menu header, so it is doubled.
            var item = new MenuItem { Header = Shorten(r.Label).Replace("_", "__"), Tag = r.Id, ToolTip = r.Label };
            item.Click += CmGo_Click;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------- comments and attachments ----------

    private int _commentVersion;

    private async void CommentsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RunAsync(async () =>
    {
        var version = ++_commentVersion;
        if (CommentsList.SelectedItem is not Workspace.CommentItem item || ObjectGrid.SelectedItem is not RowViewModel row || _workspace is null) { CommentPreview.Children.Clear(); return; }
        var embeds = await _workspace.LoadEmbedsAsync(row.Id, item.Body, item.Id);
        if (version != _commentVersion) return;
        RenderMarkup(CommentPreview, item.Body, embeds);
    });

    private async void CommentNew_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        var text = Dialogs.Prompt(this, "New comment", $"Comment on {row.CanonicalId}  (formatting works as in object text; attach a picture or CSV to the object and use its embed code to show it here):", "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        await _workspace!.AddCommentAsync(row.Id, null, text);
        await ReloadAsync(row.Id);
        SetStatus($"Comment added to {row.CanonicalId}. Comments are permanent; resolve a thread when it is dealt with.");
    });

    private async void CommentReply_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        if (CommentsList.SelectedItem is not Workspace.CommentItem item) { SetStatus("Select a comment in the Comments tab to reply to."); return; }
        var text = Dialogs.Prompt(this, "Reply", "Your reply:", "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        await _workspace!.AddCommentAsync(row.Id, item.ThreadId, text);
        await ReloadAsync(row.Id);
    });

    private async void CommentAttach_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        if (CommentsList.SelectedItem is not Workspace.CommentItem comment) { SetStatus("Select the comment to attach the file to."); return; }
        var dialog = new OpenFileDialog { Title = "Attach a file to the comment", Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var item = await _workspace!.AttachFileAsync(_service, row.Id, dialog.FileName, comment.Id);
        await ReloadAsync(row.Id);
        SetStatus($"Attached {item.FileName} to the comment. To show it inside the comment, select it in the Attachments tab and use Copy embed code.");
    });

    private async void CommentResolve_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        if (CommentsList.SelectedItem is not Workspace.CommentItem item) { SetStatus("Select a comment of the thread in the Comments tab."); return; }
        await _workspace!.SetThreadResolvedAsync(item.ThreadId, !item.Resolved);
        await ReloadAsync(row.Id);
        SetStatus(item.Resolved ? "Thread reopened." : "Thread resolved.");
    });

    private async void AttachFile_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        var dialog = new OpenFileDialog { Title = $"Attach a file to {row.CanonicalId}", Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var item = await _workspace!.AttachFileAsync(_service, row.Id, dialog.FileName);
        await ReloadAsync(row.Id);
        SetStatus($"Attached {item.FileName} ({item.Size}) to {row.CanonicalId}. A copy is stored inside the project.");
    });

    private async void AttachOpen_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (AttachmentsList.SelectedItem is not Workspace.AttachmentItem item) { SetStatus("Select an attachment first."); return Task.CompletedTask; }
        var file = _workspace!.AttachmentFile(item);
        if (file is null) { SetStatus($"The stored file for {item.FileName} is missing from the project folder."); return Task.CompletedTask; }
        if (ObjectAttachment.IsRisky(item.FileName)) { SetStatus($"{item.FileName} is a program or script type, so TraceForge will not launch it. Use Save copy… if you really need the file."); return Task.CompletedTask; }
        // Copy to a temp name with the original file name so the associated app opens it; the project's own copy is never opened for writing.
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TraceForge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        _tempFolders.Add(temp);
        var copy = System.IO.Path.Combine(temp, ObjectAttachment.CleanName(item.FileName));
        System.IO.File.Copy(file, copy);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(copy) { UseShellExecute = true });
        SetStatus($"Opened a temporary copy of {item.FileName}. Changes made there are not saved into the project.");
        return Task.CompletedTask;
    });

    private void AttachCopyEmbed_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentsList.SelectedItem is not Workspace.AttachmentItem item) { SetStatus("Select an attachment first."); return; }
        var kind = Markup.IsTableFile(item.FileName) ? "table" : Markup.ImageMime(item.FileName) is not null ? "picture" : null;
        if (kind is null) { SetStatus("Only pictures (png, jpg, gif, bmp, webp) and CSV/TSV tables can be shown inside text."); return; }
        var caption = System.IO.Path.GetFileNameWithoutExtension(item.FileName);
        try { Clipboard.SetText($"![{caption}](attach:{item.FileName})"); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Another program is holding the clipboard; try again."); return; }
        SetStatus($"Copied the code for the {kind}. Edit the object's text (Ctrl+E), paste it on its own line, and see Text preview.");
    }

    private async void AttachSave_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (AttachmentsList.SelectedItem is not Workspace.AttachmentItem item) { SetStatus("Select an attachment first."); return Task.CompletedTask; }
        var file = _workspace!.AttachmentFile(item);
        if (file is null) { SetStatus($"The stored file for {item.FileName} is missing from the project folder."); return Task.CompletedTask; }
        var dialog = new SaveFileDialog { Title = "Save a copy of the attachment", FileName = item.FileName, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        System.IO.File.Copy(file, dialog.FileName, true);
        SetStatus($"Saved a copy to {dialog.FileName}.");
        return Task.CompletedTask;
    });

    private async void AttachRemove_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row || AttachmentsList.SelectedItem is not Workspace.AttachmentItem item) { SetStatus("Select an attachment first."); return; }
        if (MessageBox.Show(this, $"Remove {item.FileName} from {row.CanonicalId}?\n\nThe file stays inside the project folder; only the attachment is hidden.", "Remove attachment", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await _workspace!.RemoveAttachmentAsync(item);
        await ReloadAsync(row.Id);
        SetStatus($"Removed {item.FileName} from {row.CanonicalId}.");
    });

    private async void TraceTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TraceTree.SelectedItem is TraceItem { Id: { } id }) await RunAsync(() => NavigateToAsync(id));
    }

    private async void LinksList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LinksList.SelectedItem is LinkItem item) await RunAsync(() => NavigateToAsync(item.OtherId));
    }

    private async void LinkGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (LinksList.SelectedItem is LinkItem item) await RunAsync(() => NavigateToAsync(item.OtherId)); else SetStatus("Select a link in the list first.");
    }

    private async void LinkRemove_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (LinksList.SelectedItem is not LinkItem item) { SetStatus("Select a link in the list first."); return; }
        if (MessageBox.Show(this, $"Remove this {item.Type} link ({item.Direction}) to:\n{item.Text}?", "Remove link", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await _workspace!.RemoveLinkAsync(_module!, item);
        await ReloadAsync((ObjectGrid.SelectedItem as RowViewModel)?.Id);
        SetStatus("Link removed.");
    });

    /// <summary>Selects the module that holds the object (clearing any filter) and puts the object in view.</summary>
    private async Task NavigateToAsync(ObjectId id)
    {
        if (_workspace is null) return;
        var obj = await _workspace.FindObjectAsync(id);
        if (obj?.ContainerId is not { } containerId) { SetStatus("That object is not in a module."); return; }
        if (_module?.Id != containerId)
        {
            var node = _nodes.FirstOrDefault(n => n.Container.Id == containerId);
            if (node is null) { SetStatus("Could not find the module of that object."); return; }
            SelectNode(node);
        }
        ClearFilterControls();
        await ReloadAsync(id);
        SetStatus($"Jumped to {obj.CanonicalId} in {_module!.Name}.");
    }

    private async void AuditLog_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_workspace is null) return;
        var rows = await _workspace.LoadAuditAsync(1000);
        Dialogs.ShowTable(this, "Audit log", $"The newest {rows.Count} entries of this project's append-only audit log (newest first). Entries can never be edited or deleted.", rows,
            ("When", nameof(Workspace.AuditRow.When), 140), ("By", nameof(Workspace.AuditRow.Actor), 90), ("Action", nameof(Workspace.AuditRow.Action), 150), ("Detail", nameof(Workspace.AuditRow.Detail), 480));
    });

    private async void GoTo_Click(object sender, RoutedEventArgs e) => await RunAsync(GoToAsync);

    private async Task GoToAsync()
    {
        if (_workspace is null) return;
        var found = await _workspace.LoadSearchTextAsync();
        if (found.Count == 0) { SetStatus("There are no objects yet."); return; }
        var items = found.Select(f => new Dialogs.PickItem(f.Id, f.Text)).ToList();
        var result = Dialogs.PickMany(this, "Go to object", "Type part of an ID, text, module name or attribute value (for example Priority=high), then choose:", items, single: true);
        if (result is { } r && r.Picked.Count > 0) await NavigateToAsync((ObjectId)r.Picked[0].Tag);
    }

    // ---------- linking ----------

    private async void Link_Click(object sender, RoutedEventArgs e) => await RunAsync(LinkAsync);
    private async void LinkParent_Click(object sender, RoutedEventArgs e) => await RunAsync(LinkParentAsync);

    private async Task LinkAsync()
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select the parent object to link from."); return; }
        var children = await _workspace!.ChildModulesAsync(_module!);
        if (children.Count == 0) { SetStatus("This module has no child module. Use Module, New Child Module of Selected to add the next level."); return; }
        var target = children.Count == 1 ? children[0] : Dialogs.Choose(this, "Link to child module", "Child module:", children, m => m.Name);
        if (target is null) return;
        if (await PickLinkObjectsAsync(target, "Link to child objects", $"Objects in {target.Name} that {row.CanonicalId} links down to (check one or more):", $"{target.Name} has no objects yet. Select it and add some first.") is not { } pick) return;
        var type = pick.Type;
        var (created, skipped) = await _workspace.LinkAsync(_module!, pick.Objects.Select(o => (row.Id, o.Object.Id)), type);
        await ReloadAsync(row.Id);
        SetStatus($"Linked {row.CanonicalId} \u2192 {created} object(s) as {type}" + (skipped > 0 ? $"; {skipped} already linked." : "."));
    }

    /// <summary>Lets the user check objects of another module to link with and choose the link type. Null when there is nothing to pick or the dialog was cancelled.</summary>
    private async Task<(List<ModuleObject> Objects, RelationshipType Type)?> PickLinkObjectsAsync(Container other, string title, string label, string emptyMessage)
    {
        var objects = await _workspace!.ListModuleObjectsAsync(other);
        if (objects.Count == 0) { SetStatus(emptyMessage); return null; }
        var items = objects.Select(x => new Dialogs.PickItem(x, $"{x.Number} {x.Object.CanonicalId}  {x.Object.Title}".Trim())).ToList();
        var picked = Dialogs.PickMany(this, title, label, items, withLinkType: true);
        if (picked is null) return null;
        return (picked.Value.Picked.Select(p => (ModuleObject)p.Tag).ToList(), picked.Value.Type ?? RelationshipType.Decomposes);
    }

    private async Task LinkParentAsync()
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select the child object to link to."); return; }
        var parent = await _workspace!.ParentModuleAsync(_module!);
        if (parent is null) { SetStatus("This module has no parent module, so nothing can link down to it."); return; }
        if (await PickLinkObjectsAsync(parent, "Link to parent objects", $"Objects in {parent.Name} that link down to {row.CanonicalId} (check one or more):", $"{parent.Name} has no objects yet.") is not { } pick) return;
        var type = pick.Type;
        var (created, skipped) = await _workspace.LinkAsync(_module!, pick.Objects.Select(o => (o.Object.Id, row.Id)), type);
        await ReloadAsync(row.Id);
        SetStatus($"Linked {created} parent object(s) \u2192 {row.CanonicalId} as {type}" + (skipped > 0 ? $"; {skipped} already linked." : "."));
    }

    private void ObjectGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow && source is not DataGridColumnHeader) source = ParentOf(source);
        if (source is DataGridRow gridRow) gridRow.IsSelected = true;
    }

    private void ObjectGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (!_attributeColumns.TryGetValue(e.Column, out var attribute) || e.Row.Item is not RowViewModel row || e.EditingElement is not TextBox box) return;
        var text = box.Text;
        // The binding is one-way, so the grid itself changes nothing; we persist after the edit ends and then reload.
        _ = Dispatcher.InvokeAsync(() => RunAsync(async () =>
        {
            if (_workspace is null) return;
            try { await _workspace.SetValueAsync(_module!, row.Id, attribute, row[attribute.Id.ToString()], text); }
            catch (ArgumentException ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, attribute.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
            await ReloadAsync(row.Id);
        }));
    }

    // ---------- delete, copy, paste ----------

    private async void Delete_Click(object sender, RoutedEventArgs e) => await RunAsync(DeleteAsync);
    private async void Copy_Click(object sender, RoutedEventArgs e) => await RunAsync(CopyAsync);
    private async void Paste_Click(object sender, RoutedEventArgs e) => await RunAsync(() => PasteAsync(false));
    private async void PasteChild_Click(object sender, RoutedEventArgs e) => await RunAsync(() => PasteAsync(true));
    private async void Deleted_Click(object sender, RoutedEventArgs e) => await RunAsync(DeletedAsync);

    private int SectionSize(RowViewModel row)
    {
        var rows = _view?.Rows; if (rows is null) return 1;
        var start = rows.ToList().FindIndex(r => r.Id == row.Id);
        var count = 1;
        for (var i = start + 1; start >= 0 && i < rows.Count && rows[i].Depth > row.Depth; i++) count++;
        return count;
    }

    private async Task DeleteAsync()
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object to delete first."); return; }
        var size = SectionSize(row);
        var what = size == 1 ? $"{row.Number} {row.CanonicalId}" : $"{row.Number} {row.CanonicalId} and the {size - 1} object(s) below it";
        if (MessageBox.Show(this, $"Delete {what}?\n\nNothing is destroyed: you can bring it back with Module, Deleted Objects.", "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var rows = ObjectGrid.Items.OfType<RowViewModel>().ToList();
        var index = rows.FindIndex(r => r.Id == row.Id);
        var count = (await _workspace!.DeleteAsync(_module!, row.Id)).Count;
        await ReloadAsync(null);
        var after = ObjectGrid.Items.OfType<RowViewModel>().ToList();
        if (after.Count > 0) { var next = after[Math.Min(Math.Max(index, 0), after.Count - 1)]; ObjectGrid.SelectedItem = next; ObjectGrid.ScrollIntoView(next); }
        SetStatus($"Deleted {count} object(s). Restore them with Module, Deleted Objects.");
    }

    private async Task DeletedAsync()
    {
        if (!NeedModule()) return;
        var deleted = await _workspace!.ListDeletedAsync(_module!);
        if (deleted.Count == 0) { SetStatus($"{_module!.Name} has no deleted objects."); return; }
        var items = deleted.Select(d => new Dialogs.PickItem(d, $"{d.CanonicalId}  {d.Title}   ({d.Count} object(s), deleted {d.DeletedUtc.ToLocalTime():yyyy-MM-dd HH:mm})")).ToList();
        var result = Dialogs.PickMany(this, "Deleted objects", "Choose a deleted section to restore it as the last child of where it was:", items, single: true);
        if (result is not { } r || r.Picked.Count == 0) return;
        var section = (DeletedSection)r.Picked[0].Tag;
        var count = await _workspace.RestoreAsync(_module!, section);
        await ReloadAsync(section.RootId);
        SetStatus($"Restored {count} object(s).");
    }

    private async Task CopyAsync()
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object to copy first."); return; }
        var count = await _workspace!.CopyAsync(_module!, row.Id);
        try { Clipboard.SetText(_workspace.ClipboardText()); } catch (System.Runtime.InteropServices.ExternalException) { /* another program holds the clipboard; the in-app copy still works */ }
        SetStatus($"Copied {count} object(s). Paste with Ctrl+V (below) or Ctrl+Shift+V (as child), in this or any other module.");
    }

    private async Task PasteAsync(bool asChild)
    {
        if (!NeedModule()) return;
        if (_workspace!.Clipboard is null) { SetStatus("Nothing has been copied yet."); return; }
        var selected = ObjectGrid.SelectedItem as RowViewModel;
        if (asChild && selected is null) { SetStatus("Select the object that should receive the pasted section."); return; }
        var (root, count, skipped) = await _workspace.PasteAsync(_module!, selected?.Id, asChild);
        ClearFilterControls();
        await ReloadAsync(root);
        SetStatus($"Pasted {count} object(s) with new IDs (links are not copied)." + (skipped > 0 ? $" {skipped} attribute value(s) were left out because this module does not use those attributes." : ""));
    }

    // ---------- objects and outline ----------

    private async void AddHeading_Click(object sender, RoutedEventArgs e) => await RunAsync(() => AddAsync(true, false));
    private async void AddObject_Click(object sender, RoutedEventArgs e) => await RunAsync(() => AddAsync(false, false));
    private async void AddChild_Click(object sender, RoutedEventArgs e) => await RunAsync(() => AddAsync(false, true));
    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await RunAsync(() => MoveAsync(MoveKind.Up));
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await RunAsync(() => MoveAsync(MoveKind.Down));
    private async void Indent_Click(object sender, RoutedEventArgs e) => await RunAsync(() => MoveAsync(MoveKind.Indent));
    private async void Outdent_Click(object sender, RoutedEventArgs e) => await RunAsync(() => MoveAsync(MoveKind.Outdent));

    private async Task AddAsync(bool heading, bool asChild)
    {
        if (!NeedModule()) return;
        var text = Dialogs.Prompt(this, heading ? "Add heading" : "Add object", "Text:");
        if (string.IsNullOrWhiteSpace(text)) return;
        var selected = ObjectGrid.SelectedItem as RowViewModel;
        if (asChild && selected is null) { SetStatus("Select the object that should get a child first."); return; }
        var id = await _workspace!.AddAsync(_module!, text, heading, selected?.Id, asChild);
        await ReloadAsync(id);
    }

    private async Task MoveAsync(MoveKind kind)
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        if (!await _workspace!.MoveAsync(_module!, row.Id, kind)) { SetStatus($"Cannot move {row.Number} {kind.ToString().ToLowerInvariant()}."); return; }
        await ReloadAsync(row.Id);
        if (_sortKey is not null) SetStatus($"Moved {row.CanonicalId} in the outline. The table is sorted by a column, so the move is not visible until you restore outline order (Clear).");
    }

    // ---------- history and baselines ----------

    private async void EditText_Click(object sender, RoutedEventArgs e) => await RunAsync(EditTextAsync);

    private async Task EditTextAsync()
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        var text = Dialogs.Prompt(this, "Edit object text", $"Text of {row.CanonicalId}  (formatting: **bold**, *italic*, lines starting with \"- \" are bullets; Enter starts a new line):", row.Title, multiline: true);
        if (string.IsNullOrWhiteSpace(text) || text == row.Title) return;
        await _workspace!.RenameAsync(_module!, row.Id, text);
        await ReloadAsync(row.Id);
        SetStatus($"Updated {row.CanonicalId}. The previous text is kept in its history.");
    }

    private async void CreateBaseline_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var name = Dialogs.Prompt(this, "Create baseline", $"Name for the baseline of {_module!.Name} (for example Release 1):");
        if (string.IsNullOrWhiteSpace(name)) return;
        var note = Dialogs.Prompt(this, "Create baseline", "Optional note:") ?? "";
        var baseline = await _workspace!.CreateBaselineAsync(_module, name, note);
        SetStatus($"Created baseline \u201C{baseline.Name}\u201D of {_module.Name}. Baselines are frozen and cannot be edited or deleted.");
    });

    private async void Suspects_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var suspects = await _workspace!.LoadSuspectsAsync(_module!);
        if (suspects.Count == 0) { SetStatus("No suspect links: nothing linked to this module has changed since its link was reviewed."); return; }
        var items = suspects.Select(s => new Dialogs.PickItem(s, $"{s.Source}  \u2192  {s.Target}   [{s.Type}; {s.Reason}]")).ToList();
        var picked = Dialogs.PickMany(this, "Suspect links", "Tick the links you have reviewed and click OK to clear their suspect flag:", items);
        if (picked is null) return;
        var ids = picked.Value.Picked.Select(p => ((Workspace.SuspectRow)p.Tag).RelationshipId).ToList();
        await _workspace.ClearSuspectAsync(ids);
        await ReloadAsync((ObjectGrid.SelectedItem as RowViewModel)?.Id);
        SetStatus($"Marked {ids.Count} link(s) as reviewed.");
    });

    private async Task ShowImpactAsync(bool downstream)
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        var rows = await _workspace!.ImpactAsync(row.Id, downstream);
        SetStatus($"{rows.Count} object(s) {(downstream ? "downstream of" : "upstream of")} {row.CanonicalId}.");
        Dialogs.ShowTable(this, downstream ? "Impact (downstream)" : "Impact (upstream)",
            downstream ? $"{row.CanonicalId}: objects that may need rework if this one changes \u2014 {rows.Count}" : $"{row.CanonicalId}: objects this one derives from \u2014 {rows.Count}", rows,
            ("Depth", nameof(Workspace.ImpactRow.Depth), 50), ("Module", nameof(Workspace.ImpactRow.Module), 120), ("Object", nameof(Workspace.ImpactRow.Object), 360), ("Via", nameof(Workspace.ImpactRow.Via), 180));
    }

    private async void ImpactDown_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ShowImpactAsync(true));
    private async void ImpactUp_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ShowImpactAsync(false));

    private async void CoverageSummary_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var report = await _workspace!.LoadCoverageAsync(_module!);
        var rows = Workspace.SummaryRows(report);
        SetStatus($"Coverage of {_module!.Name} and its child levels: {report.Gaps.Count} gap(s).");
        Dialogs.ShowTable(this, "Coverage summary", $"{_module.Name} and its child levels \u2014 objects with links to the next level down / from the level above ({report.Gaps.Count} gap(s); see Traceability Gaps).", rows,
            ("Module", nameof(Workspace.CoverageRow.Module), 220), ("Objects", nameof(Workspace.CoverageRow.Total), 70), ("With links down", nameof(Workspace.CoverageRow.Downstream), 200), ("With links up", nameof(Workspace.CoverageRow.Upstream), 200));
    });

    private async void CoverageGaps_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var report = await _workspace!.LoadCoverageAsync(_module!);
        SetStatus($"{report.Gaps.Count} traceability gap(s) in {_module!.Name} and its child levels.");
        Dialogs.ShowTable(this, "Traceability gaps", $"{_module.Name} and its child levels \u2014 {report.Gaps.Count} object(s) missing an expected link.", report.Gaps,
            ("Module", nameof(CoverageGap.ModuleName), 130), ("Number", nameof(CoverageGap.Number), 70), ("ID", nameof(CoverageGap.CanonicalId), 100), ("Text", nameof(CoverageGap.Title), 300), ("Gap", nameof(CoverageGap.Description), 250));
    });

    private async void CoverageExport_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var report = await _workspace!.LoadCoverageAsync(_module!);
        var dialog = new SaveFileDialog { Title = "Export traceability gaps", Filter = "CSV files (*.csv)|*.csv", FileName = _module!.Name + " gaps.csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await Workspace.ExportCoverageFileAsync(report, dialog.FileName);
        SetStatus($"Exported {report.Gaps.Count} gap(s) to {dialog.FileName}.");
    });

    private async void CompareCurrent_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var baselines = await _workspace!.ListBaselinesAsync(_module!);
        if (baselines.Count == 0) { SetStatus($"{_module!.Name} has no baselines yet (History, Create Baseline)."); return; }
        var older = Dialogs.Choose(this, "Compare with current", "Baseline to compare against the current state:", baselines, b => $"{b.Name}  ({b.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
        if (older is null) return;
        var rows = await _workspace.CompareAsync(_module!, older, null);
        SetStatus($"{rows.Count} change(s) since baseline {older.Name}.");
        Dialogs.ShowChanges(this, "Changes since baseline", $"{_module!.Name}: baseline \u201C{older.Name}\u201D \u2192 current state \u2014 {rows.Count} change(s)", rows);
    });

    private async void CompareTwo_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var baselines = await _workspace!.ListBaselinesAsync(_module!);
        if (baselines.Count < 2) { SetStatus("Comparing two baselines needs at least two baselines for this module."); return; }
        string Label(Baseline b) => $"{b.Name}  ({b.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
        var older = Dialogs.Choose(this, "Compare baselines", "Older baseline:", baselines, Label);
        if (older is null) return;
        var newer = Dialogs.Choose(this, "Compare baselines", "Newer baseline:", baselines.Where(b => b.Id != older.Id).ToList(), Label);
        if (newer is null) return;
        var rows = await _workspace.CompareAsync(_module!, older, newer);
        SetStatus($"{rows.Count} change(s) between {older.Name} and {newer.Name}.");
        Dialogs.ShowChanges(this, "Changes between baselines", $"{_module!.Name}: \u201C{older.Name}\u201D \u2192 \u201C{newer.Name}\u201D \u2014 {rows.Count} change(s)", rows);
    });

    private async void ViewBaseline_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var baselines = await _workspace!.ListBaselinesAsync(_module!);
        if (baselines.Count == 0) { SetStatus($"{_module!.Name} has no baselines yet (History, Create Baseline)."); return; }
        var pick = Dialogs.Choose(this, "View baseline", "Baseline to view:", baselines, b => $"{b.Name}  ({b.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
        if (pick is null) return;
        var rows = await _workspace.ViewBaselineAsync(pick);
        Dialogs.ShowTable(this, "Baseline", $"{_module!.Name} \u2014 baseline \u201C{pick.Name}\u201D, created {pick.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" + (pick.Note.Length > 0 ? $". {pick.Note}" : "") + $". {rows.Count} object(s), frozen.",
            rows, ("Number", nameof(BaselineRow.Number), 70), ("ID", nameof(BaselineRow.Id), 100), ("Text", nameof(BaselineRow.Text), 360), ("Attributes", nameof(BaselineRow.Attributes), 300));
    });

    private async void CreateBaselineSet_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var modules = await _workspace!.ModuleAndDescendantsAsync(_module!);
        var name = Dialogs.Prompt(this, "Create baseline set", $"Name for the baseline of {_module!.Name} and its {modules.Count - 1} child module(s) (for example Release 1):");
        if (string.IsNullOrWhiteSpace(name)) return;
        var note = Dialogs.Prompt(this, "Create baseline set", "Optional note:") ?? "";
        var created = await _workspace.CreateBaselineSetAsync(_module, name, note);
        SetStatus($"Created baseline \u201C{name.Trim()}\u201D for {created.Count} module(s) at once.");
    });

    private async void CompareSet_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var names = await _workspace!.BaselineNamesInSetAsync(_module!);
        if (names.Count == 0) { SetStatus("Neither this module nor its child modules have baselines yet."); return; }
        var name = Dialogs.Choose(this, "Compare baseline set", "Baseline name (compared across this module and its child modules):", names, n => n);
        if (name is null) return;
        var rows = await _workspace.CompareSetAsync(_module!, name);
        SetStatus($"{rows.Count} change(s) across the set since baseline {name}.");
        Dialogs.ShowChanges(this, "Changes since baseline set", $"{_module!.Name} and child modules: baseline \u201C{name}\u201D \u2192 current state \u2014 {rows.Count} change(s)", rows);
    });

    // ---------- attributes ----------

    private async void NewAttribute_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var definition = Dialogs.PromptAttribute(this);
        if (definition is null) return;
        await _workspace!.Store.AddAttributeDefinitionAsync(definition);
        await _workspace.Store.AssignAttributeToModuleAsync(_module!.Id, definition.Id);
        await ReloadAsync(null);
    });

    private async void AddExistingAttribute_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var assigned = (await _workspace!.Store.ListModuleAttributesAsync(_module!.Id)).Select(a => a.Id).ToHashSet();
        var available = (await _workspace.Store.ListAttributeDefinitionsAsync()).Where(a => !assigned.Contains(a.Id)).ToList();
        if (available.Count == 0) { SetStatus("Every defined attribute is already used by this module."); return; }
        var choice = Dialogs.Choose(this, "Add attribute", "Attribute to add as a column:", available, a => $"{a.Name} ({a.DataType})");
        if (choice is null) return;
        await _workspace.Store.AssignAttributeToModuleAsync(_module.Id, choice.Id);
        await ReloadAsync(null);
    });

    // ---------- undo / redo ----------

    private void MenuEdit_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        var stack = _workspace?.Undo;
        MenuUndo.IsEnabled = stack?.CanUndo == true; MenuRedo.IsEnabled = stack?.CanRedo == true;
        // An underscore in a menu header marks an access key, so underscores in module names are doubled to show as written.
        MenuUndo.Header = stack?.UndoText is { } u ? $"_Undo {u.Replace("_", "__")}" : "_Undo";
        MenuRedo.Header = stack?.RedoText is { } r ? $"_Redo {r.Replace("_", "__")}" : "_Redo";
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await RunAsync(() => UndoRedoAsync(false));
    private async void Redo_Click(object sender, RoutedEventArgs e) => await RunAsync(() => UndoRedoAsync(true));

    private async Task UndoRedoAsync(bool redo)
    {
        if (_workspace is null) return;
        var stack = _workspace.Undo;
        if (!(redo ? stack.CanRedo : stack.CanUndo)) { SetStatus(redo ? "Nothing to redo." : "Nothing to undo."); return; }
        UndoEntry? entry;
        try { entry = redo ? await stack.RedoAsync() : await stack.UndoAsync(); }
        catch { await RefreshTreeAsync(_module?.Id); throw; } // the project changed underneath the entry; show the truth and report the error
        if (entry is null) return;
        if (entry.RefreshTree)
        {
            // A rename, move, delete or restore in the module tree: reload the tree and select the item it concerned.
            await RefreshTreeAsync(entry.ModuleId);
            SetStatus($"{(redo ? "Redid" : "Undid")}: {entry.Description}.");
            return;
        }
        if (entry.ModuleId is { } moduleId && _module?.Id != moduleId && _nodes.FirstOrDefault(n => n.Container.Id == moduleId) is { } node)
        {
            SelectNode(node);
            ClearFilterControls();
        }
        await ReloadAsync(entry.SelectId);
        SetStatus($"{(redo ? "Redid" : "Undid")}: {entry.Description}.");
    }

    // ---------- import / export ----------

    private async void ImportCsv_Click(object sender, RoutedEventArgs e) => await RunAsync(ImportCsvAsync);
    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await RunAsync(ExportCsvAsync);
    private async void ExportHtml_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var dialog = new SaveFileDialog { Title = "Export HTML report", Filter = "HTML files (*.html)|*.html", FileName = _module!.Name + ".html", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await _workspace!.ExportHtmlFileAsync(_module, dialog.FileName);
        SetStatus($"Wrote {dialog.FileName}. Open it in a browser; use Print there for a paper or PDF copy.");
    });

    private async void ExportAll_Click(object sender, RoutedEventArgs e) => await RunAsync(ExportAllAsync);

    private async Task ImportCsvAsync()
    {
        if (!NeedModule()) return;
        var dialog = new OpenFileDialog { Title = "Import a CSV file into " + _module!.Name, Filter = "CSV files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        await ImportTableAsync(Csv.Parse(await System.IO.File.ReadAllTextAsync(dialog.FileName)), System.IO.Path.GetFileName(dialog.FileName));
    }

    private async void ImportReqIf_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var dialog = new OpenFileDialog { Title = "Import a ReqIF file into " + _module!.Name, Filter = "ReqIF files (*.reqif;*.reqifz;*.xml)|*.reqif;*.reqifz;*.xml|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileName.EndsWith(".reqifz", StringComparison.OrdinalIgnoreCase)) { SetStatus("Zipped .reqifz files are not supported yet; unzip it and import the .reqif file inside."); return; }
        var info = new System.IO.FileInfo(dialog.FileName);
        if (info.Length > MaxReqIfBytes) { SetStatus("That file is larger than 100 MB, which TraceForge will not import."); return; }
        var first = ReqIf.Read(await System.IO.File.ReadAllTextAsync(dialog.FileName));
        var import = first;
        if (first.SpecificationNames.Count > 1)
        {
            var names = first.SpecificationNames.Select((n, i) => $"{i + 1}. {n}").ToList();
            var pick = Dialogs.Choose(this, "Choose a specification", "This file has several documents. Import which one?", names, n => n);
            if (pick is null) return;
            import = ReqIf.Read(await System.IO.File.ReadAllTextAsync(dialog.FileName), names.IndexOf(pick));
        }
        if (import.Warnings.Count > 0)
            if (MessageBox.Show(this, string.Join("\n", import.Warnings) + "\n\nContinue with the import preview?", "ReqIF notes", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await ImportTableAsync(import.Table, System.IO.Path.GetFileName(dialog.FileName));
    });

    private async Task ImportTableAsync(List<List<string>> table, string fileLabel)
    {
        var context = await _workspace!.BuildImportContextAsync(_module!);
        var plan = Dialogs.ConfirmImport(this, "Import into " + _module!.Name, fileLabel, create => ImportPlanner.Plan(table, context with { CreateUnknownAttributes = create }));
        if (plan is null) return;
        var outcome = await _workspace.ApplyImportAsync(_module, plan);
        ClearFilterControls();
        await ReloadAsync(outcome.Created.FirstOrDefault());
        SetStatus($"Imported into {_module.Name}: {outcome.Created.Count} created, {outcome.Updated.Count} updated, {outcome.LinkIds.Count} link(s)" + (outcome.NewAttributes > 0 ? $", {outcome.NewAttributes} new attribute(s)." : ".") + " Edit, Undo reverses the whole import.");
    }

    private async void ExportReqIf_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        var dialog = new SaveFileDialog { Title = "Export ReqIF", Filter = "ReqIF files (*.reqif)|*.reqif", FileName = _module!.Name + ".reqif", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var count = await _workspace!.ExportReqIfFileAsync(_module, dialog.FileName);
        SetStatus($"Exported {count} object(s) of {_module.Name} and its child levels to {dialog.FileName}. Attribute types are written as text.");
    });

    private async Task ExportCsvAsync()
    {
        if (!NeedModule()) return;
        var dialog = new SaveFileDialog { Title = "Export " + _module!.Name, Filter = "CSV files (*.csv)|*.csv", FileName = _module.Name + ".csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await _workspace!.ExportCsvFileAsync(_module, dialog.FileName);
        SetStatus($"Exported {_module.Name} to {dialog.FileName} (UTF-8; open it in Excel with Data, From Text/CSV if accents look wrong).");
    }

    private async Task ExportAllAsync()
    {
        if (_workspace is null) return;
        var dialog = new OpenFolderDialog { Title = "Choose the folder for one CSV file per module" };
        if (dialog.ShowDialog(this) != true) return;
        var written = await _workspace.ExportProjectAsync(dialog.FolderName);
        SetStatus($"Exported {written.Count} module(s) to {dialog.FolderName}.");
    }

    private async void RestoreRevision_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!NeedModule()) return;
        if (ObjectGrid.SelectedItem is not RowViewModel row) { SetStatus("Select an object first."); return; }
        if (HistoryList.SelectedItem is not HistoryItem item) { SetStatus("Select a revision in the History tab first."); return; }
        if (HistoryList.Items.Count > 0 && HistoryList.Items[0] is HistoryItem newest && newest.Revision == item.Revision) { SetStatus("That is already the current revision."); return; }
        if (MessageBox.Show(this, $"Make {row.CanonicalId} equal to revision {item.Revision} ({item.When})?\n\nThis adds a new revision; nothing in the history is removed, and Undo reverses it.", "Restore revision", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var changed = await _workspace!.RestoreRevisionAsync(_module!, row.Id, item.Revision);
        await ReloadAsync(row.Id);
        SetStatus(changed ? $"Restored revision {item.Revision} of {row.CanonicalId} as a new revision." : $"{row.CanonicalId} already matches revision {item.Revision}.");
    });

    // ---------- keyboard ----------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; ShowHelp(); return; }
        if (Keyboard.FocusedElement is TextBox) return; // never steal keys while typing in a cell or dialog field
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var alt = Keyboard.Modifiers == ModifierKeys.Alt;
        Func<Task>? action = null;
        if (ctrl && key == Key.N) action = NewProjectAsync;
        else if (ctrl && key == Key.O) action = OpenProjectAsync;
        else if (ctrl && key == Key.L) action = LinkAsync;
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.L) action = LinkParentAsync;
        else if (ctrl && key == Key.G) action = GoToAsync;
        else if (ctrl && key == Key.E) action = EditTextAsync;
        else if (ctrl && key == Key.Z) action = () => UndoRedoAsync(false);
        else if (ctrl && key == Key.Y) action = () => UndoRedoAsync(true);
        else if (ObjectGrid.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.None && key == Key.Delete) action = DeleteAsync;
        else if (ObjectGrid.IsKeyboardFocusWithin && ctrl && key == Key.C) action = CopyAsync;
        else if (ObjectGrid.IsKeyboardFocusWithin && ctrl && key == Key.V) action = () => PasteAsync(false);
        else if (ObjectGrid.IsKeyboardFocusWithin && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.V) action = () => PasteAsync(true);
        else if (ctrl && key == Key.F) { FilterText.Focus(); e.Handled = true; return; }
        else if (ModuleTree.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.None && key == Key.F2) action = RenameContainerAsync;
        else if (ModuleTree.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.None && key == Key.Delete) action = DeleteContainerAsync;
        else if (ModuleTree.IsKeyboardFocusWithin && alt && key == Key.Up) action = () => NudgeContainerAsync(-1);
        else if (ModuleTree.IsKeyboardFocusWithin && alt && key == Key.Down) action = () => NudgeContainerAsync(1);
        else if (alt && key == Key.Up) action = () => MoveAsync(MoveKind.Up);
        else if (alt && key == Key.Down) action = () => MoveAsync(MoveKind.Down);
        else if (ctrl && key == Key.Right) action = () => MoveAsync(MoveKind.Indent);
        else if (ctrl && key == Key.Left) action = () => MoveAsync(MoveKind.Outdent);
        if (action is null) return;
        e.Handled = true;
        await RunAsync(action);
    }
}
