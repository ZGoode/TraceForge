using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TraceForge.Domain;

namespace TraceForge.Desktop;

// Look-and-feel code: View menu options (remembered between runs), drop-down toolbar buttons and the right-click menus.
public partial class MainWindow
{
    // Column positions of the fixed grid columns (see MainWindow.xaml).
    private const int ColInLinks = 6, ColOutLinks = 7, ColNotes = 8;

    private sealed class UiSettings
    {
        public bool LinkIds { get; set; } = true;
        public bool Notes { get; set; } = true;
        public bool Details { get; set; } = true;
        public bool Compact { get; set; }
        public bool Large { get; set; }
        public bool ShowDeleted { get; set; }
    }

    private UiSettings _ui = new();

    /// <summary>Builds the column-header right-click menu in code: WPF cannot wire Click handlers declared inside a Style.</summary>
    private void BuildHeaderMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string header, RoutedEventHandler click) { var item = new MenuItem { Header = header }; item.Click += click; menu.Items.Add(item); return item; }
        Add("Sort ascending", HeaderSortAsc_Click);
        Add("Sort descending", HeaderSortDesc_Click);
        Add("Back to outline order", HeaderSortClear_Click);
        menu.Items.Add(new Separator());
        Add("Hide this column", HeaderHide_Click);
        Add("Choose columns\u2026", Columns_Click);
        Add("Show all columns", ShowAllColumns_Click);
        var style = new Style(typeof(DataGridColumnHeader), (Style)FindResource("GridHeader"));
        style.Setters.Add(new Setter(ContextMenuProperty, menu));
        ObjectGrid.ColumnHeaderStyle = style;
    }

    private static string UiPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraceForge", "ui.json");

    private void LoadUi()
    {
        try { if (File.Exists(UiPath)) _ui = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiPath)) ?? new UiSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _ui = new UiSettings(); }
        ApplyUi();
    }

    private void SaveUi()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiPath)!);
            File.WriteAllText(UiPath, JsonSerializer.Serialize(_ui));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a preference that cannot be saved is not worth an error */ }
    }

    private void ApplyUi()
    {
        ViewLinkIds.IsChecked = _ui.LinkIds; ViewNotes.IsChecked = _ui.Notes; ViewDetails.IsChecked = _ui.Details;
        ViewCompact.IsChecked = _ui.Compact; ViewLarge.IsChecked = _ui.Large; ViewDeleted.IsChecked = _ui.ShowDeleted;
        var columns = ObjectGrid.Columns;
        if (columns.Count > ColNotes)
        {
            columns[ColInLinks].Visibility = columns[ColOutLinks].Visibility = _ui.LinkIds ? Visibility.Visible : Visibility.Collapsed;
            columns[ColNotes].Visibility = _ui.Notes ? Visibility.Visible : Visibility.Collapsed;
        }
        FontSize = _ui.Large ? 16 : 13;
        ObjectGrid.RowHeight = (_ui.Compact ? 22 : 30) + (_ui.Large ? 4 : 0);
        DetailsRow.Height = _ui.Details ? new GridLength(230) : new GridLength(0);
        DetailsSplitter.Visibility = DetailsTabs.Visibility = _ui.Details ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ViewOption_Click(object sender, RoutedEventArgs e)
    {
        var deletedBefore = _ui.ShowDeleted;
        _ui.LinkIds = ViewLinkIds.IsChecked; _ui.Notes = ViewNotes.IsChecked; _ui.Details = ViewDetails.IsChecked;
        _ui.Compact = ViewCompact.IsChecked; _ui.Large = ViewLarge.IsChecked; _ui.ShowDeleted = ViewDeleted.IsChecked;
        ApplyUi(); SaveUi();
        if (deletedBefore != _ui.ShowDeleted && _workspace is not null) await RunAsync(() => RefreshTreeAsync(SelectedNode?.Container.Id));
    }

    private void ShowAllColumns_Click(object sender, RoutedEventArgs e)
    {
        _ui.LinkIds = true; _ui.Notes = true; _shownIds = null;
        ApplyUi(); SaveUi();
        if (_view is not null) ApplyFilter();
    }

    /// <summary>Opens the drop-down menu attached to a toolbar button just below it.</summary>
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------- right-click helpers ----------

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>Right-clicking a list or tree item selects it first, so the menu acts on what is under the mouse.</summary>
    private void SelectOnRightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem && source is not TreeViewItem) source = ParentOf(source);
        switch (source)
        {
            case ListBoxItem item: item.IsSelected = true; item.Focus(); break;
            case TreeViewItem node: node.IsSelected = true; node.Focus(); break;
        }
    }

    private static string Shorten(string text, int max = 70)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private void SetClipboard(string text, string what)
    {
        try { Clipboard.SetText(text); SetStatus($"Copied {what} to the clipboard."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("The clipboard is busy; try again."); }
    }

    // ---------- grid menu ----------

    private void GridMenu_Opened(object sender, RoutedEventArgs e)
    {
        var haveModule = _workspace is not null && _module is not null;
        var row = haveModule ? ObjectGrid.SelectedItem as RowViewModel : null;
        foreach (var item in GridMenu.Items.OfType<MenuItem>()) item.IsEnabled = haveModule;
        foreach (var item in new[] { CmEdit, CmAddChild, CmCopy, CmPasteChild, CmDelete, CmMove, CmLinks, CmComment, CmAttach, CmShow, CmCopyId, CmCopyText })
            item.IsEnabled = row is not null;
        FillGoMenu(CmGoIn, row?.InRefs);
        FillGoMenu(CmGoOut, row?.OutRefs);
    }

    private void FillGoMenu(MenuItem menu, IReadOnlyList<LinkRef>? refs)
    {
        menu.Items.Clear();
        menu.IsEnabled = refs is { Count: > 0 };
        if (refs is null) return;
        foreach (var r in refs)
        {
            var item = new MenuItem { Header = Shorten(r.Label).Replace("_", "__"), Tag = r.Id, ToolTip = r.Label };
            item.Click += CmGo_Click;
            menu.Items.Add(item);
        }
    }

    private async void CmGo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ObjectId id }) await RunAsync(() => NavigateToAsync(id));
    }

    private void CmTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        if (!_ui.Details) { _ui.Details = true; ApplyUi(); SaveUi(); }
        DetailsTabs.SelectedIndex = Math.Clamp(index, 0, DetailsTabs.Items.Count - 1);
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectGrid.SelectedItem is RowViewModel row) SetClipboard(row.CanonicalId, row.CanonicalId);
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectGrid.SelectedItem is RowViewModel row) SetClipboard(row.Title, "the object text");
    }

    // ---------- column header menu ----------

    private DataGridColumn? HeaderColumn(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is DataGridColumnHeader { Column: { } column } ? column : null;

    private void SortBy(object sender, bool descending)
    {
        if (HeaderColumn(sender) is not { } column || KeyOf(column) is not { } key) return;
        _sortKey = key; _sortDescending = descending;
        if (_view is not null) ApplyFilter();
    }

    private void HeaderSortAsc_Click(object sender, RoutedEventArgs e) => SortBy(sender, false);
    private void HeaderSortDesc_Click(object sender, RoutedEventArgs e) => SortBy(sender, true);

    private void HeaderSortClear_Click(object sender, RoutedEventArgs e)
    {
        _sortKey = null;
        if (_view is not null) ApplyFilter();
    }

    private void HeaderHide_Click(object sender, RoutedEventArgs e)
    {
        if (HeaderColumn(sender) is not { } column) return;
        var index = ObjectGrid.Columns.IndexOf(column);
        if (index == ColInLinks || index == ColOutLinks) { _ui.LinkIds = false; ApplyUi(); SaveUi(); }
        else if (index == ColNotes) { _ui.Notes = false; ApplyUi(); SaveUi(); }
        else if (_attributeColumns.TryGetValue(column, out var attribute) && _view is not null)
        {
            var ids = (_shownIds ?? _view.Attributes.Select(a => a.Id.ToString()).ToList()).Where(id => id != attribute.Id.ToString()).ToList();
            _shownIds = ids;
            ApplyFilter();
        }
        else SetStatus("This column is always shown. Use View, Show all columns to bring hidden ones back.");
    }

    // ---------- module tree menu ----------

    private void ModuleMenu_Opened(object sender, RoutedEventArgs e)
    {
        var open = _workspace is not null;
        var module = open && _module is not null;
        var node = open && SelectedNode is not null; // a folder can be selected too: it can receive new modules and be renamed, moved or deleted
        CmNewModule.IsEnabled = open; CmNewFolder.IsEnabled = open; CmModDeleted.IsEnabled = module;
        CmNewChild.IsEnabled = node; CmNewSubfolder.IsEnabled = node;
        CmRename.IsEnabled = node; CmContainerUp.IsEnabled = node; CmContainerDown.IsEnabled = node; CmContainerMove.IsEnabled = node; var deletedNode = node && SelectedNode!.IsDeleted;
        CmContainerDelete.IsEnabled = node && !deletedNode; CmContainerRestore.IsEnabled = deletedNode; CmContainerPurge.IsEnabled = deletedNode; CmContainerLinks.IsEnabled = open;
        CmModImport.IsEnabled = module; CmModExport.IsEnabled = module; CmModReports.IsEnabled = module; CmModBaseline.IsEnabled = module;
    }

    // ---------- detail list menus ----------

    private async void TraceGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (TraceTree.SelectedItem is TraceItem { Id: { } id }) await RunAsync(() => NavigateToAsync(id));
        else SetStatus("Select an object in the trace first.");
    }

    private void TraceCopy_Click(object sender, RoutedEventArgs e)
    {
        if (TraceTree.SelectedItem is TraceItem item) SetClipboard(item.Text, "the line");
    }

    private void ListCopy_Click(object sender, RoutedEventArgs e)
    {
        var list = ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as ListView;
        var text = list?.SelectedItem switch
        {
            LinkItem l => l.Text,
            HistoryItem h => h.Summary,
            Workspace.CommentItem c => c.Body,
            _ => null,
        };
        if (text is null) SetStatus("Select a line in the list first."); else SetClipboard(text, "the text");
    }
}
