using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TraceForge.Domain;

namespace TraceForge.Desktop;

// Drag and drop: rearranging objects in the module table and modules/folders in the tree.
// Drop on the upper third of a row/node = before it, lower third = after it, middle = inside it (last child).
public partial class MainWindow
{
    private const string RowFormat = "TraceForge.Row", ContainerFormat = "TraceForge.Container";
    private Point _dragOrigin;
    private object? _dragCandidate; // the RowViewModel or ContainerNode the left button went down on
    private bool _dragging;

    private void WireDragDrop()
    {
        ObjectGrid.AllowDrop = true; ModuleTree.AllowDrop = true;
        ObjectGrid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragOrigin = e.GetPosition(null);
            _dragCandidate = e.OriginalSource is DependencyObject d && Ancestor<TextBox>(d) is null ? Ancestor<DataGridRow>(d)?.Item as RowViewModel : null; // not while editing a cell
        };
        ObjectGrid.PreviewMouseMove += Grid_PreviewMouseMove;
        ObjectGrid.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(RowFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        ObjectGrid.Drop += Grid_Drop;

        ModuleTree.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragOrigin = e.GetPosition(null);
            _dragCandidate = e.OriginalSource is DependencyObject d ? Ancestor<TreeViewItem>(d)?.DataContext as ContainerNode : null;
        };
        ModuleTree.PreviewMouseMove += Tree_PreviewMouseMove;
        ModuleTree.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(ContainerFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        ModuleTree.Drop += Tree_Drop;
    }

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = ParentOf(d);
        return d as T;
    }

    private bool MovedFarEnough(MouseEventArgs e)
    {
        var p = e.GetPosition(null);
        return Math.Abs(p.X - _dragOrigin.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - _dragOrigin.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private void StartDrag(DependencyObject source, string format, string payload)
    {
        _dragging = true; _dragCandidate = null;
        try { DragDrop.DoDragDrop(source, new DataObject(format, payload), DragDropEffects.Move); }
        finally { _dragging = false; }
    }

    private static DropPlace PlaceFor(double fraction) => fraction < 0.33 ? DropPlace.Before : fraction > 0.67 ? DropPlace.After : DropPlace.Inside;

    private static string Describe(DropPlace place) => place switch { DropPlace.Before => "before", DropPlace.After => "after", _ => "inside" };

    // ---------- module table ----------

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not RowViewModel row || !MovedFarEnough(e)) return;
        if (_sortKey is not null || (_view is not null && BuildDefinition().IsFiltering))
        {
            _dragCandidate = null;
            SetStatus("Clear the sort and the filter (Clear) before rearranging by dragging: the table has to show the outline order.");
            return;
        }
        StartDrag(ObjectGrid, RowFormat, row.Id.ToString());
    }

    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RowFormat) || _workspace is null || _module is null) return;
        e.Handled = true;
        var dragged = ObjectId.Parse((string)e.Data.GetData(RowFormat)!);
        var targetRow = Ancestor<DataGridRow>(ObjectGrid.InputHitTest(e.GetPosition(ObjectGrid)) as DependencyObject);
        if (targetRow?.Item is not RowViewModel target) return;
        var place = PlaceFor(e.GetPosition(targetRow).Y / Math.Max(1, targetRow.ActualHeight));
        var workspace = _workspace; var module = _module;
        await RunAsync(async () =>
        {
            if (!await workspace.DropAsync(module, dragged, target.Id, place)) { SetStatus("Nothing moved."); return; }
            await ReloadAsync(dragged);
            SetStatus($"Moved the object {Describe(place)} {target.CanonicalId}. Undo (Ctrl+Z) puts it back.");
        });
    }

    // ---------- module tree ----------

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not ContainerNode node || !MovedFarEnough(e)) return;
        StartDrag(ModuleTree, ContainerFormat, node.Container.Id.ToString());
    }

    private async void Tree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ContainerFormat) || _workspace is null) return;
        e.Handled = true;
        var id = ObjectId.Parse((string)e.Data.GetData(ContainerFormat)!);
        var dragged = _nodes.FirstOrDefault(n => n.Container.Id == id);
        if (dragged is null) return;
        var item = Ancestor<TreeViewItem>(ModuleTree.InputHitTest(e.GetPosition(ModuleTree)) as DependencyObject);
        var workspace = _workspace;
        var place = DropPlace.Inside;
        if (item is not null)
        {
            var header = item.Template?.FindName("PART_Header", item) as FrameworkElement;
            place = header is { ActualHeight: > 0 } ? PlaceFor(e.GetPosition(header).Y / header.ActualHeight) : DropPlace.Inside;
        }
        await RunAsync(async () =>
        {
            if (item?.DataContext is ContainerNode target)
            {
                if (!await workspace.DropContainerAsync(dragged.Container, target.Container, place)) return;
                SetStatus($"Moved {dragged.Name} {Describe(place)} {target.Name}.");
            }
            else
            {
                await workspace.MoveContainerAsync(dragged.Container, null); // dropped on empty space: top level, last
                SetStatus($"Moved {dragged.Name} to the top level.");
            }
            await RefreshTreeAsync(dragged.Container.Id);
        });
    }
}
