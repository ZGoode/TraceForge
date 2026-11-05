using System.Text;
using System.Windows;
using TraceForge.Application;
using TraceForge.Domain;
using TraceForge.Infrastructure;

namespace TraceForge.Desktop;

public enum MoveKind { Up, Down, Indent, Outdent }

/// <summary>Where a dragged entry lands relative to the entry it is dropped on.</summary>
public enum DropPlace { Before, After, Inside }

/// <summary>One line of a module table: an object in outline order with its attribute values and link summaries.</summary>
public sealed class RowViewModel(ObjectId id, string number, string title, string canonicalId, int depth, bool isHeading,
    Dictionary<string, string> values, string? missingText, int inLinks, int outLinks, string inSummary, string outSummary, int suspectLinks = 0, int openComments = 0, int attachments = 0,
    IReadOnlyList<LinkRef>? inRefs = null, IReadOnlyList<LinkRef>? outRefs = null)
{
    public ObjectId Id { get; } = id;
    public string Number { get; } = number;
    public string Title { get; } = title;
    /// <summary>Title without formatting markers, on one line, for the grid.</summary>
    public string Display => Markup.Plain(Title);
    public string CanonicalId { get; } = canonicalId;
    public int Depth { get; } = depth;
    public bool IsHeading { get; } = isHeading;
    public string? MissingText { get; } = missingText;
    public int InLinks { get; } = inLinks;
    public int OutLinks { get; } = outLinks;
    public string InSummary { get; } = inSummary;
    public string OutSummary { get; } = outSummary;
    public IReadOnlyDictionary<string, string> Values { get; } = values;
    public IReadOnlyList<LinkRef> InRefs { get; } = inRefs ?? [];
    public IReadOnlyList<LinkRef> OutRefs { get; } = outRefs ?? [];
    /// <summary>DOORS-style link markers: ◀ (parents link to this) and ▶ (this links to children), with a count when there are several. Click opens a list of the linked objects.</summary>
    public string InArrow => InRefs.Count == 0 ? "" : InRefs.Count == 1 ? "\u25C0" : "\u25C0" + InRefs.Count;
    public string OutArrow => OutRefs.Count == 0 ? "" : OutRefs.Count == 1 ? "\u25B6" : "\u25B6" + OutRefs.Count;
    public string InTip => InRefs.Count == 0 ? "" : "Linked from (click to choose):\n" + string.Join("\n", InRefs.Select(r => r.Label));
    public string OutTip => OutRefs.Count == 0 ? "" : "Links to (click to choose):\n" + string.Join("\n", OutRefs.Select(r => r.Label));
    public int SuspectLinks { get; } = suspectLinks;
    public int OpenComments { get; } = openComments;
    public int Attachments { get; } = attachments;
    public string Notes => (OpenComments > 0 ? $"\U0001F4AC{OpenComments} " : "") + (Attachments > 0 ? $"\U0001F4CE{Attachments}" : "");
    public string Flag => (MissingText is null ? "" : "⚠") + (SuspectLinks > 0 ? "⚑" : "");
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public FontWeight Weight => IsHeading ? FontWeights.Bold : FontWeights.Normal;

    /// <summary>Attribute values keyed by attribute id (text form, "N" guid). Missing keys read as empty.</summary>
    public string this[string attributeId] => Values.TryGetValue(attributeId, out var v) ? v : "";

    public RowFacts Facts => new(Number, CanonicalId, Title, Values, InLinks, OutLinks, MissingText is not null);
}

/// <summary>The other end of a link, with a label such as "Decomposes \u2192 SDD-003  text [Design]".</summary>
public sealed record LinkRef(ObjectId Id, string Label);

/// <summary>A folder or module in the project tree. Modules nested under modules are the document levels.</summary>
public sealed class ContainerNode(Container container) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _selected;
    public Container Container { get; } = container;
    public string Name => Container.Name;
    public bool IsDeleted => Container.IsDeleted;
    /// <summary>What the tree shows: deleted items are marked so they stand out even without the colour.</summary>
    public string Label => IsDeleted ? Container.Name + "  (deleted)" : Container.Name;
    /// <summary>Deleted items get a light red background in the tree.</summary>
    public System.Windows.Media.Brush Background => IsDeleted ? DeletedBrush : System.Windows.Media.Brushes.Transparent;
    private static readonly System.Windows.Media.Brush DeletedBrush = MakeDeletedBrush();
    private static System.Windows.Media.Brush MakeDeletedBrush() { var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD9, 0xD9)); b.Freeze(); return b; }
    public FontWeight Weight => Container.Kind == ContainerKind.Folder ? FontWeights.Bold : FontWeights.Normal;
    public List<ContainerNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ModuleObject(EngineeringObject Object, string Number);

/// <summary>A line of the trace tree. Id lets the UI jump to the object.</summary>
public sealed record TraceItem(ObjectId? Id, string Kind, string Text, List<TraceItem> Children);

/// <summary>One link of the selected object, as shown in the Links list.</summary>
public sealed record LinkItem(ObjectId RelationshipId, string Direction, string Type, ObjectId OtherId, string Text, string ModuleName, ObjectId SourceId, ObjectId TargetId, RelationshipType RelType);

/// <summary>One revision of the selected object, as shown in the History tab.</summary>
public sealed record HistoryItem(long Revision, string When, string Actor, string Summary, string Title);

/// <summary>One line of a baseline comparison.</summary>
public sealed record ChangeRow(string Change, string Object, string Description, string Module = "");

/// <summary>One object of a frozen baseline, as shown when a baseline is viewed on its own.</summary>
public sealed record BaselineRow(string Number, string Id, string Text, string Attributes);

/// <summary>A copied object of the in-app clipboard. ParentIndex refers to an earlier item.</summary>
public sealed record ClipItem(int? ParentIndex, string Title, bool IsHeading, string Number, IReadOnlyDictionary<string, string> Values);

public sealed record ModuleView(IReadOnlyList<RowViewModel> Rows, IReadOnlyList<AttributeDefinition> Attributes, IReadOnlyList<SavedView> Views);

/// <summary>UI-free application logic for the open project: loads module tables and applies edits through the store.</summary>
public sealed class Workspace(IProjectStore store, string path) : IAsyncDisposable
{
    public IProjectStore Store { get; } = store;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    // ---------- containers ----------

    public async Task<Container> CreateContainerAsync(string name, ContainerKind kind, ObjectId? parentId)
    {
        var container = Container.Create(kind, name, parentId);
        await Store.AddContainerAsync(container);
        return container;
    }

    /// <summary>Tree order of containers: the order the user arranged (sort order), then name. Projects that were never rearranged are all zero, so they stay alphabetical.
    /// Must match the store's own sibling order (SqliteProjectStore.OrderedSiblings).</summary>
    public static IEnumerable<Container> InTreeOrder(IEnumerable<Container> containers) =>
        containers.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id.ToString(), StringComparer.Ordinal);

    /// <summary>All folders and modules as a tree (modules under modules are document levels).</summary>
    public async Task<List<ContainerNode>> LoadTreeAsync(bool includeDeleted = false)
    {
        // Deleted items sort after the live ones at each level (InTreeOrder keeps their old order among themselves).
        var source = includeDeleted ? await Store.ListAllContainersAsync() : await Store.ListContainersAsync();
        var containers = InTreeOrder(source.Where(c => !c.IsDeleted)).Concat(InTreeOrder(source.Where(c => c.IsDeleted))).ToList();
        var all = containers.Select(c => new ContainerNode(c)).ToList();
        var byId = all.ToDictionary(n => n.Container.Id);
        var roots = new List<ContainerNode>();
        foreach (var node in all)
            if (node.Container.ParentId is { } p && byId.TryGetValue(p, out var parent)) parent.Children.Add(node); else roots.Add(node);
        return roots;
    }

    // Rename, move, delete and restore of folders and modules are undoable; permanent delete is not (and clears the undo history).

    private void PushContainerUndo(string description, ObjectId containerId, Func<Task> undo, Func<Task> redo) =>
        Undo.Push(new UndoEntry(description, containerId, null, undo, redo, RefreshTree: true));

    /// <summary>Where a live folder or module sits now: its parent and its position among the live siblings.</summary>
    private async Task<(ObjectId? Parent, int Index)> ContainerPositionAsync(ObjectId id)
    {
        var all = (await Store.ListContainersAsync()).ToList();
        var me = all.FirstOrDefault(c => c.Id == id);
        return me is null ? (null, 0) : (me.ParentId, InTreeOrder(all.Where(c => c.ParentId == me.ParentId)).ToList().FindIndex(c => c.Id == id));
    }

    public async Task RenameContainerAsync(Container container, string name)
    {
        var oldName = container.Name;
        name = name.Trim();
        await Store.RenameContainerAsync(container.Id, name);
        if (oldName == name) return;
        PushContainerUndo($"Rename {oldName} to {name}", container.Id,
            () => Store.RenameContainerAsync(container.Id, oldName),
            () => Store.RenameContainerAsync(container.Id, name));
    }

    /// <summary>Moves a folder or module under <paramref name="newParentId"/> (null = top level), last among its siblings unless an index is given.</summary>
    public async Task MoveContainerAsync(Container container, ObjectId? newParentId, int index = int.MaxValue)
    {
        var (oldParent, oldIndex) = await ContainerPositionAsync(container.Id);
        await Store.MoveContainerAsync(container.Id, newParentId, index);
        var (parent, position) = await ContainerPositionAsync(container.Id);
        if (oldParent == parent && oldIndex == position) return;
        PushContainerUndo($"Move {container.Name}", container.Id,
            () => Store.MoveContainerAsync(container.Id, oldParent, oldIndex),
            () => Store.MoveContainerAsync(container.Id, parent, position));
    }

    /// <summary>Moves a folder or module one place up (-1) or down (+1) among its siblings. False when it is already first or last.</summary>
    public async Task<bool> NudgeContainerAsync(Container container, int delta)
    {
        if (container.IsDeleted) throw new InvalidOperationException("This item is deleted. Restore it before moving it.");
        var siblings = InTreeOrder((await Store.ListContainersAsync()).Where(c => c.ParentId == container.ParentId)).ToList();
        var i = siblings.FindIndex(c => c.Id == container.Id);
        var target = i + delta;
        if (i < 0 || target < 0 || target >= siblings.Count) return false;
        await MoveContainerAsync(container, container.ParentId, target); // index among the siblings without the moved one
        return true;
    }

    /// <summary>Marks the folder or module (and what is inside) as deleted. It stays visible under View, Show Deleted, and can be restored or deleted permanently.
    /// Undo restores it to the place it had among its siblings.</summary>
    public async Task SoftDeleteContainerAsync(Container container)
    {
        var (parent, index) = await ContainerPositionAsync(container.Id);
        await Store.SoftDeleteContainerAsync(container.Id);
        PushContainerUndo($"Delete {container.Name}", container.Id,
            async () => { await Store.RestoreContainerAsync(container.Id); await Store.MoveContainerAsync(container.Id, parent, index); },
            () => Store.SoftDeleteContainerAsync(container.Id));
    }

    public async Task RestoreContainerAsync(Container container)
    {
        await Store.RestoreContainerAsync(container.Id);
        PushContainerUndo($"Restore {container.Name}", container.Id,
            () => Store.SoftDeleteContainerAsync(container.Id),
            () => Store.RestoreContainerAsync(container.Id));
    }

    /// <summary>Destroys a deleted folder or module. Earlier undo entries may refer to what is gone, so the whole undo history is cleared.</summary>
    public async Task PurgeContainerAsync(Container container)
    {
        await Store.PurgeContainerAsync(container.Id);
        Undo.Clear();
    }

    public sealed record DeletedLinkRow(string From, string To, string Type, string Direction);

    /// <summary>Links with exactly one end inside a deleted module. With a <paramref name="root"/> the "inside" is that module and everything below it
    /// (what Delete Permanently would cut off from modules that stay); without one it is every deleted module.</summary>
    public async Task<List<DeletedLinkRow>> LinksIntoDeletedAsync(Container? root)
    {
        var containers = (await Store.ListAllContainersAsync()).ToList();
        var inside = new HashSet<ObjectId>();
        if (root is null) foreach (var deleted in containers.Where(d => d.IsDeleted)) inside.Add(deleted.Id);
        else
        {
            inside.Add(root.Id);
            var grew = true;
            while (grew) { grew = false; foreach (var c in containers) if (c.ParentId is { } p && inside.Contains(p) && inside.Add(c.Id)) grew = true; }
        }
        var names = containers.ToDictionary(c => c.Id, c => c.Name);
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        bool Inside(ObjectId id) => objects.TryGetValue(id, out var o) && o.ContainerId is { } c && inside.Contains(c);
        string Label(ObjectId id) => objects.TryGetValue(id, out var o)
            ? $"{o.CanonicalId} {Markup.Plain(o.Title)}   [{(o.ContainerId is { } c ? names.GetValueOrDefault(c, "?") : "?")}]" : id.ToString();
        return (await Store.ListRelationshipsAsync()).Where(r => Inside(r.SourceId) != Inside(r.TargetId))
            .Select(r => new DeletedLinkRow(Label(r.SourceId), Label(r.TargetId), r.Type.ToString(), Inside(r.SourceId) ? "from the deleted module" : "into the deleted module")).ToList();
    }

    /// <summary>How many live objects a module and everything below it hold (shown in the delete confirmations).</summary>
    public async Task<int> CountObjectsUnderAsync(Container container)
    {
        var all = await Store.ListAllContainersAsync();
        var ids = new HashSet<ObjectId> { container.Id };
        var grew = true;
        while (grew) { grew = false; foreach (var c in all) if (c.ParentId is { } p && ids.Contains(p) && ids.Add(c.Id)) grew = true; }
        var total = 0;
        foreach (var id in ids) total += (await Store.ListModuleObjectsAsync(id)).Count;
        return total;
    }

    /// <summary>Gives <paramref name="target"/> every attribute column of <paramref name="source"/> that it does not have yet. Returns how many were added.</summary>
    public async Task<int> CopyAttributeColumnsAsync(Container target, Container source)
    {
        var have = (await Store.ListModuleAttributesAsync(target.Id)).Select(a => a.Id).ToHashSet();
        var added = 0;
        foreach (var attribute in await Store.ListModuleAttributesAsync(source.Id))
            if (!have.Contains(attribute.Id)) { await Store.AssignAttributeToModuleAsync(target.Id, attribute.Id); added++; }
        return added;
    }

    public async Task<IReadOnlyList<Container>> ChildModulesAsync(Container module) =>
        InTreeOrder((await Store.ListContainersAsync()).Where(c => c.Kind == ContainerKind.Module && c.ParentId == module.Id)).ToList();

    public async Task<Container?> ParentModuleAsync(Container module) =>
        module.ParentId is { } p ? (await Store.ListContainersAsync()).FirstOrDefault(c => c.Id == p && c.Kind == ContainerKind.Module) : null;

    public async Task<EngineeringObject?> FindObjectAsync(ObjectId id) => (await Store.ListObjectsAsync()).FirstOrDefault(o => o.Id == id);

    public async Task<IReadOnlyList<ModuleObject>> ListModuleObjectsAsync(Container module)
    {
        var numbers = Outline.Number(await Store.GetOutlineAsync(module.Id));
        return (await Store.ListModuleObjectsAsync(module.Id))
            .OrderBy(o => numbers.ContainsKey(o.Id) ? 0 : 1).ThenBy(o => numbers.GetValueOrDefault(o.Id, ""), NaturalOrder.Instance).ThenBy(o => o.CanonicalId, NaturalOrder.Instance)
            .Select(o => new ModuleObject(o, numbers.GetValueOrDefault(o.Id, ""))).ToList();
    }

    // ---------- module table ----------

    /// <summary>Attribute values of the module's objects: object id to (attribute id in "N" text form to canonical value).</summary>
    private async Task<Dictionary<ObjectId, Dictionary<string, string>>> ValuesByObjectAsync(ObjectId moduleId) =>
        (await Store.ListAttributeValuesAsync(moduleId)).GroupBy(v => v.ObjectId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.AttributeId.ToString(), v => v.Value));

    /// <summary>Which objects are at the other end, by ID. The default type (Decomposes) is not written out; other types are shown as "Type: IDs".</summary>
    private static string Summarize(IEnumerable<TypedRelationship> links, Func<TypedRelationship, ObjectId> otherEnd, IReadOnlyDictionary<ObjectId, EngineeringObject> all)
    {
        string Id(TypedRelationship r) => all.TryGetValue(otherEnd(r), out var o) ? o.CanonicalId : "?";
        var groups = links.GroupBy(r => r.Type).OrderBy(g => (int)g.Key).ToList();
        return string.Join("; ", groups.Select(g => (g.Key == RelationshipType.Decomposes ? "" : g.Key + ": ") + string.Join(", ", g.Select(Id).OrderBy(x => x, NaturalOrder.Instance))));
    }

    public async Task<ModuleView> LoadModuleAsync(Container module)
    {
        var entries = await Store.GetOutlineAsync(module.Id);
        var allObjects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        var allContainers = (await Store.ListAllContainersAsync()).ToDictionary(c => c.Id, c => c.Name);
        var objects = (await Store.ListModuleObjectsAsync(module.Id)).ToDictionary(o => o.Id);
        foreach (var o in objects.Values) allObjects[o.Id] = o; // also when the module itself is deleted
        var attributes = await Store.ListModuleAttributesAsync(module.Id);
        var names = attributes.ToDictionary(a => a.Id, a => a.Name);
        var numbers = Outline.Number(entries);
        var relationships = await Store.ListRelationshipsAsync();
        var outLinks = relationships.ToLookup(r => r.SourceId);
        var inLinks = relationships.ToLookup(r => r.TargetId);
        string Ref(TypedRelationship r, ObjectId other, string arrow) => allObjects.TryGetValue(other, out var x)
            ? $"{arrow} {x.CanonicalId}  {Markup.Plain(x.Title)}   [{(x.ContainerId is { } cid ? allContainers.GetValueOrDefault(cid, "?") : "?")}]   ({r.Type})" : $"{arrow} {other}";
        var openComments = await Store.CountOpenThreadsAsync(module.Id);
        var attachmentCounts = await Store.CountAttachmentsAsync(module.Id);
        var suspectCount = new Dictionary<ObjectId, int>();
        foreach (var sl in await Store.ListSuspectLinksAsync(module.Id))
            foreach (var end in new[] { sl.SourceId, sl.TargetId }) suspectCount[end] = suspectCount.GetValueOrDefault(end) + 1;

        var valuesByObject = await ValuesByObjectAsync(module.Id);
        var missingByObject = (await Store.FindMissingRequiredValuesAsync(module.Id)).GroupBy(m => m.ObjectId)
            .ToDictionary(g => g.Key, g => "Missing required value: " + string.Join(", ", g.Select(m => names.GetValueOrDefault(m.AttributeId, "?"))));

        var children = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        var rows = new List<RowViewModel>();
        void Walk(IEnumerable<OutlineEntry> siblings, int depth)
        {
            foreach (var e in siblings.OrderBy(x => x.SortOrder).ThenBy(x => x.ObjectId.ToString(), StringComparer.Ordinal))
            {
                if (objects.TryGetValue(e.ObjectId, out var o))
                    rows.Add(new RowViewModel(o.Id, numbers.GetValueOrDefault(o.Id, ""), o.Title, o.CanonicalId, depth, e.IsHeading,
                        valuesByObject.GetValueOrDefault(o.Id) ?? new Dictionary<string, string>(), missingByObject.GetValueOrDefault(o.Id),
                        inLinks[o.Id].Count(), outLinks[o.Id].Count(), Summarize(inLinks[o.Id], r => r.SourceId, allObjects), Summarize(outLinks[o.Id], r => r.TargetId, allObjects), suspectCount.GetValueOrDefault(o.Id), openComments.GetValueOrDefault(o.Id), attachmentCounts.GetValueOrDefault(o.Id),
                        inLinks[o.Id].Select(r => new LinkRef(r.SourceId, Ref(r, r.SourceId, "\u25C0 from"))).ToList(), outLinks[o.Id].Select(r => new LinkRef(r.TargetId, Ref(r, r.TargetId, "\u25B6 to"))).ToList()));
                Walk(children[e.ObjectId], depth + 1);
            }
        }
        Walk(entries.Where(e => e.ParentId is null), 0);
        return new ModuleView(rows, attributes, await Store.ListViewsAsync(module.Id));
    }

    // ---------- undo ----------

    /// <summary>In-session undo/redo of edits made through this workspace. Cleared when the project closes.</summary>
    public UndoStack Undo { get; } = new();

    private async Task<(ObjectId? Parent, int Index)> PositionAsync(ObjectId moduleId, ObjectId objectId)
    {
        var entries = await Store.GetOutlineAsync(moduleId);
        var me = entries.SingleOrDefault(e => e.ObjectId == objectId);
        return me is null ? (null, 0) : (me.ParentId, Siblings(entries, me.ParentId).FindIndex(e => e.ObjectId == objectId));
    }

    private async Task<long> RevisionAsync(ObjectId objectId) => (await FindObjectAsync(objectId))?.Revision ?? 0;

    /// <summary>Registers the undo of "a section was created or restored": undo deletes it, redo restores it where it was.</summary>
    private async Task PushSectionCreatedAsync(string description, Container module, ObjectId root)
    {
        var position = await PositionAsync(module.Id, root);
        ObjectId? batch = null;
        Undo.Push(new UndoEntry(description, module.Id, root,
            async () => { batch = (await Store.DeleteSectionAsync(module.Id, root)).BatchId; },
            async () => { await Store.RestoreDeletedAsync(batch!.Value, position.Index); }));
    }

    // ---------- objects and outline ----------

    private (ObjectId? Parent, int? Index) Target(IReadOnlyList<OutlineEntry> entries, ObjectId? selectedId, bool asChild)
    {
        if (selectedId is not { } selected) return (null, null);
        var current = entries.Single(e => e.ObjectId == selected);
        if (asChild) return (selected, null);
        return (current.ParentId, Siblings(entries, current.ParentId).FindIndex(e => e.ObjectId == selected) + 1);
    }

    /// <summary>Adds a new object. With no selection it goes at the end of the top level; otherwise below the selection, or as its last child.</summary>
    public async Task<ObjectId> AddAsync(Container module, string title, bool isHeading, ObjectId? selectedId, bool asChild)
    {
        var (parent, index) = Target(await Store.GetOutlineAsync(module.Id), selectedId, asChild);
        var obj = EngineeringObject.Create(EngineeringObjectType.Requirement, await NextCanonicalIdAsync(module), title, module.Id);
        await Store.AddSectionAsync(module.Id, parent, index, [new SectionItem(obj, null, isHeading, new Dictionary<string, string>())]);
        await PushSectionCreatedAsync($"Add {(isHeading ? "heading" : "object")} {obj.CanonicalId}", module, obj.Id);
        return obj.Id;
    }

    /// <summary>Moves an entry; returns false when the move is not possible (already first/last, nothing to indent under, already top level).</summary>
    public async Task<bool> MoveAsync(Container module, ObjectId objectId, MoveKind kind)
    {
        var entries = await Store.GetOutlineAsync(module.Id);
        var me = entries.Single(e => e.ObjectId == objectId);
        var siblings = Siblings(entries, me.ParentId);
        var i = siblings.FindIndex(e => e.ObjectId == objectId);
        ObjectId? newParent; int newIndex;
        switch (kind)
        {
            case MoveKind.Up:
                if (i <= 0) return false;
                (newParent, newIndex) = (me.ParentId, i - 1); break;
            case MoveKind.Down:
                if (i < 0 || i >= siblings.Count - 1) return false;
                (newParent, newIndex) = (me.ParentId, i + 1); break;
            case MoveKind.Indent:
                if (i <= 0) return false;
                newParent = siblings[i - 1].ObjectId; newIndex = Siblings(entries, newParent).Count; break;
            case MoveKind.Outdent:
                if (me.ParentId is not { } p) return false;
                var parent = entries.Single(e => e.ObjectId == p);
                newParent = parent.ParentId; newIndex = Siblings(entries, parent.ParentId).FindIndex(e => e.ObjectId == p) + 1; break;
            default: return false;
        }
        var oldParent = me.ParentId; var oldIndex = i;
        await Store.MoveOutlineEntryAsync(module.Id, objectId, newParent, newIndex);
        var label = (await FindObjectAsync(objectId))?.CanonicalId ?? "object";
        Undo.Push(new UndoEntry($"Move {label} ({kind.ToString().ToLowerInvariant()})", module.Id, objectId,
            () => Store.MoveOutlineEntryAsync(module.Id, objectId, oldParent, oldIndex),
            () => Store.MoveOutlineEntryAsync(module.Id, objectId, newParent, newIndex)));
        return true;
    }

    /// <summary>Moves an entry to an exact place (drag and drop). <paramref name="index"/> counts the new siblings WITHOUT the moved entry. Returns false when nothing changed.</summary>
    public async Task<bool> MoveToAsync(Container module, ObjectId objectId, ObjectId? newParent, int index)
    {
        var entries = await Store.GetOutlineAsync(module.Id);
        var me = entries.SingleOrDefault(e => e.ObjectId == objectId);
        if (me is null) return false;
        var oldParent = me.ParentId; var oldIndex = Siblings(entries, oldParent).FindIndex(e => e.ObjectId == objectId);
        await Store.MoveOutlineEntryAsync(module.Id, objectId, newParent, index); // throws when the target is the entry itself or one of its descendants
        var placed = Siblings(await Store.GetOutlineAsync(module.Id), newParent).FindIndex(e => e.ObjectId == objectId);
        if (oldParent == newParent && oldIndex == placed) return false;
        var label = (await FindObjectAsync(objectId))?.CanonicalId ?? "object";
        Undo.Push(new UndoEntry($"Move {label} (drag)", module.Id, objectId,
            () => Store.MoveOutlineEntryAsync(module.Id, objectId, oldParent, oldIndex),
            () => Store.MoveOutlineEntryAsync(module.Id, objectId, newParent, placed)));
        return true;
    }

    /// <summary>Drop of <paramref name="dragged"/> on <paramref name="target"/>: before or after it as a sibling, or inside it as its last child.</summary>
    public async Task<bool> DropAsync(Container module, ObjectId dragged, ObjectId target, DropPlace place)
    {
        if (dragged == target) return false;
        var entries = await Store.GetOutlineAsync(module.Id);
        var t = entries.SingleOrDefault(e => e.ObjectId == target);
        if (t is null) return false;
        if (place == DropPlace.Inside) return await MoveToAsync(module, dragged, target, int.MaxValue);
        var siblings = Siblings(entries, t.ParentId).Where(e => e.ObjectId != dragged).ToList();
        return await MoveToAsync(module, dragged, t.ParentId, siblings.FindIndex(e => e.ObjectId == target) + (place == DropPlace.After ? 1 : 0));
    }

    /// <summary>Drop of a folder or module on another one (before, after, or inside it as the last child). The store enforces the cycle, folder and link rules.</summary>
    public async Task<bool> DropContainerAsync(Container dragged, Container target, DropPlace place)
    {
        if (dragged.Id == target.Id) return false;
        if (place == DropPlace.Inside) { await MoveContainerAsync(dragged, target.Id, int.MaxValue); return true; }
        var siblings = InTreeOrder((await Store.ListContainersAsync()).Where(c => c.ParentId == target.ParentId && c.Id != dragged.Id)).ToList();
        await MoveContainerAsync(dragged, target.ParentId, siblings.FindIndex(c => c.Id == target.Id) + (place == DropPlace.After ? 1 : 0));
        return true;
    }

    /// <summary>Sets an attribute value (blank clears it). Registers undo only when something actually changed.</summary>
    public async Task SetValueAsync(Container module, ObjectId objectId, AttributeDefinition attribute, string? oldText, string? text)
    {
        var before = await RevisionAsync(objectId);
        await Store.SetAttributeValueAsync(objectId, attribute.Id, text);
        if (await RevisionAsync(objectId) == before) return;
        var label = (await FindObjectAsync(objectId))?.CanonicalId ?? "object";
        Undo.Push(new UndoEntry($"Set {attribute.Name} of {label}", module.Id, objectId,
            () => Store.SetAttributeValueAsync(objectId, attribute.Id, oldText),
            () => Store.SetAttributeValueAsync(objectId, attribute.Id, text)));
    }

    public async Task RenameAsync(Container module, ObjectId objectId, string title)
    {
        var obj = await FindObjectAsync(objectId) ?? throw new InvalidOperationException("The object no longer exists.");
        var oldTitle = obj.Title;
        await Store.RenameObjectAsync(objectId, title);
        if (await RevisionAsync(objectId) == obj.Revision) return;
        Undo.Push(new UndoEntry($"Edit text of {obj.CanonicalId}", module.Id, objectId,
            () => Store.RenameObjectAsync(objectId, oldTitle), () => Store.RenameObjectAsync(objectId, title)));
    }

    /// <summary>Makes the object equal to an earlier revision, as a new revision. Returns false when it already matched.</summary>
    public async Task<bool> RestoreRevisionAsync(Container module, ObjectId objectId, long revision)
    {
        var obj = await FindObjectAsync(objectId) ?? throw new InvalidOperationException("The object no longer exists.");
        var before = obj.Revision;
        if (!await Store.RestoreRevisionAsync(objectId, revision)) return false;
        var after = await RevisionAsync(objectId);
        Undo.Push(new UndoEntry($"Restore revision {revision} of {obj.CanonicalId}", module.Id, objectId,
            () => Store.RestoreRevisionAsync(objectId, before), () => Store.RestoreRevisionAsync(objectId, after)));
        return true;
    }

    private static List<OutlineEntry> Siblings(IReadOnlyList<OutlineEntry> entries, ObjectId? parent) =>
        entries.Where(e => e.ParentId == parent).OrderBy(e => e.SortOrder).ThenBy(e => e.ObjectId.ToString(), StringComparer.Ordinal).ToList();

    private async Task<List<string>> NextCanonicalIdsAsync(Container module, int count, IEnumerable<string>? avoid = null)
    {
        var taken = new HashSet<string>(await Store.ListAllCanonicalIdsAsync(), StringComparer.OrdinalIgnoreCase);
        if (avoid is not null) taken.UnionWith(avoid);
        var prefix = new string(module.Name.Where(char.IsLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();
        if (prefix.Length == 0) prefix = "OBJ";
        var n = (await Store.ListObjectsAsync()).Count(o => o.ContainerId == module.Id) + 1;
        var ids = new List<string>();
        while (ids.Count < count)
        {
            var candidate = $"{prefix}-{n:000}";
            n++;
            if (taken.Add(candidate)) ids.Add(candidate);
        }
        return ids;
    }

    private async Task<string> NextCanonicalIdAsync(Container module) => (await NextCanonicalIdsAsync(module, 1))[0];

    // ---------- links ----------

    /// <summary>Creates parent-to-child links (source = parent level object). Existing identical links are skipped.</summary>
    public async Task<(int Created, int Skipped)> LinkAsync(Container module, IEnumerable<(ObjectId Source, ObjectId Target)> pairs, RelationshipType type)
    {
        var made = new List<(ObjectId Source, ObjectId Target, ObjectId Id)>();
        var skipped = 0;
        foreach (var (source, target) in pairs)
        {
            try { made.Add((source, target, (await Store.AddLinkAsync(source, target, type)).Id)); }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) { skipped++; }
        }
        if (made.Count > 0)
        {
            var ids = made.Select(m => m.Id).ToList();
            Undo.Push(new UndoEntry($"Add {made.Count} {type} link(s)", module.Id, made[0].Source,
                async () => { foreach (var id in ids) await Store.RemoveLinkAsync(id); },
                async () => { ids.Clear(); foreach (var (source, target, _) in made) ids.Add((await Store.AddLinkAsync(source, target, type)).Id); }));
        }
        return (made.Count, skipped);
    }

    public async Task RemoveLinkAsync(Container module, LinkItem item)
    {
        await Store.RemoveLinkAsync(item.RelationshipId);
        var id = item.RelationshipId;
        Undo.Push(new UndoEntry($"Remove {item.Type} link", module.Id, item.SourceId,
            async () => { id = (await Store.AddLinkAsync(item.SourceId, item.TargetId, item.RelType)).Id; },
            async () => { await Store.RemoveLinkAsync(id); }));
    }

    public async Task<List<LinkItem>> LoadLinksAsync(ObjectId id)
    {
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        var containers = (await Store.ListAllContainersAsync()).ToDictionary(c => c.Id);
        var items = new List<LinkItem>();
        foreach (var r in (await Store.ListRelationshipsAsync()).Where(r => r.SourceId == id || r.TargetId == id).OrderBy(r => r.SourceId == id ? 0 : 1).ThenBy(r => r.CreatedUtc))
        {
            var outgoing = r.SourceId == id;
            var otherId = outgoing ? r.TargetId : r.SourceId;
            var text = objects.TryGetValue(otherId, out var o) ? $"{o.CanonicalId}  {Markup.Plain(o.Title)}" : otherId.ToString();
            var module = objects.TryGetValue(otherId, out var oo) && oo.ContainerId is { } mid && containers.TryGetValue(mid, out var m) ? m.Name : "?";
            items.Add(new LinkItem(r.Id, outgoing ? "→ child" : "← parent", r.Type.ToString(), otherId, text, module, r.SourceId, r.TargetId, r.Type));
        }
        return items;
    }

    // ---------- comments and attachments ----------

    public sealed record CommentItem(ObjectId Id, ObjectId ThreadId, bool IsReply, bool Resolved, string When, string By, string Status, string Text, string Body, string Files);
    public sealed record AttachmentItem(ObjectId RecordId, string AttachmentId, string FileName, string Size, string Added, string By, ObjectId? CommentId = null, string On = "object");

    public async Task<List<CommentItem>> LoadCommentsAsync(ObjectId objectId)
    {
        var all = await Store.ListCommentsAsync(objectId);
        var files = (await Store.ListAttachmentsAsync(objectId)).Where(a => a.CommentId is not null).ToLookup(a => a.CommentId!.Value);
        string Files(ObjectId id) => string.Join(", ", files[id].Select(a => "\U0001F4CE" + a.FileName));
        var items = new List<CommentItem>();
        string Flat(string s) => Markup.Plain(s);
        foreach (var root in all.Where(c => c.ParentId is null).OrderBy(c => c.CreatedUtc))
        {
            items.Add(new CommentItem(root.Id, root.Id, false, root.Resolved, root.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), root.Author, root.Resolved ? "resolved" : "open", Flat(root.Body), root.Body, Files(root.Id)));
            foreach (var reply in all.Where(c => c.ParentId == root.Id).OrderBy(c => c.CreatedUtc))
                items.Add(new CommentItem(reply.Id, root.Id, true, root.Resolved, reply.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), reply.Author, "", "    \u21B3 " + Flat(reply.Body), reply.Body, Files(reply.Id)));
        }
        return items;
    }

    public Task AddCommentAsync(ObjectId objectId, ObjectId? threadId, string text) => Store.AddCommentAsync(objectId, threadId, text);
    public Task SetThreadResolvedAsync(ObjectId threadId, bool resolved) => Store.SetThreadResolvedAsync(threadId, resolved);

    public async Task<AttachmentItem> AttachFileAsync(ProjectPackageService service, ObjectId objectId, string sourcePath, ObjectId? commentId = null)
    {
        var info = new System.IO.FileInfo(sourcePath);
        if (!info.Exists) throw new System.IO.FileNotFoundException("The file was not found.", sourcePath);
        if (info.Length > ObjectAttachment.MaxBytes) throw new InvalidOperationException($"Attachments are limited to {ObjectAttachment.MaxBytes / (1024 * 1024)} MB; this file is {info.Length / (1024 * 1024)} MB.");
        var managed = await service.ImportManagedAttachmentAsync(Path, sourcePath);
        var record = await Store.AddAttachmentAsync(objectId, managed.Id, System.IO.Path.GetFileName(sourcePath), managed.SizeBytes, managed.Sha256, commentId);
        return ToItem(record);
    }

    private static AttachmentItem ToItem(ObjectAttachment a) => new(a.Id, a.AttachmentId, a.FileName, a.SizeBytes < 1024 ? $"{a.SizeBytes} B" : a.SizeBytes < 1024 * 1024 ? $"{a.SizeBytes / 1024.0:0.#} KB" : $"{a.SizeBytes / 1048576.0:0.#} MB", a.AddedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), a.Actor, a.CommentId, a.CommentId is null ? "object" : "comment");

    public async Task<List<AttachmentItem>> LoadAttachmentsAsync(ObjectId objectId) => (await Store.ListAttachmentsAsync(objectId)).Select(ToItem).ToList();

    public Task RemoveAttachmentAsync(AttachmentItem item) => Store.RemoveAttachmentAsync(item.RecordId);

    /// <summary>The managed file inside the package, or null when it is missing.</summary>
    public string? AttachmentFile(AttachmentItem item)
    {
        var directory = System.IO.Path.Combine(Path, "attachments");
        if (!System.IO.Directory.Exists(directory) || item.AttachmentId.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return null;
        return System.IO.Directory.EnumerateFiles(directory, item.AttachmentId + "*").FirstOrDefault(f => !f.EndsWith(".staging", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- suspect links and impact ----------

    public sealed record SuspectRow(ObjectId RelationshipId, string Source, string Target, string Type, string Reason);
    public sealed record ImpactRow(int Depth, string Object, string Module, string Via);

    public async Task<List<SuspectRow>> LoadSuspectsAsync(Container module)
    {
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        string Name(ObjectId id) => objects.TryGetValue(id, out var o) ? $"{o.CanonicalId} {Markup.Plain(o.Title)}" : id.ToString();
        return (await Store.ListSuspectLinksAsync(module.Id)).Select(s => new SuspectRow(s.RelationshipId, Name(s.SourceId), Name(s.TargetId), s.Type.ToString(),
            s.SourceChanged && s.TargetChanged ? "both ends changed" : s.SourceChanged ? "parent object changed" : "child object changed")).ToList();
    }

    public Task ClearSuspectAsync(IReadOnlyCollection<ObjectId> ids) => Store.ClearSuspectAsync(ids);

    /// <summary>Downstream: what may need rework if this object changes. Upstream: what this object derives from.</summary>
    public async Task<List<ImpactRow>> ImpactAsync(ObjectId objectId, bool downstream)
    {
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        var containers = (await Store.ListAllContainersAsync()).ToDictionary(c => c.Id);
        string Name(ObjectId id) => objects.TryGetValue(id, out var o) ? $"{o.CanonicalId} {Markup.Plain(o.Title)}" : id.ToString();
        string ModuleOf(ObjectId id) => objects.TryGetValue(id, out var o) && o.ContainerId is { } c && containers.TryGetValue(c, out var m) ? m.Name : "?";
        return Impact.Analyze(objectId, await Store.ListRelationshipsAsync(), downstream)
            .Select(i => new ImpactRow(i.Depth, Name(i.ObjectId), ModuleOf(i.ObjectId), $"{i.ViaType} from {objects.GetValueOrDefault(i.ViaObject)?.CanonicalId}")).ToList();
    }

    // ---------- embedded pictures and tables ----------

    /// <summary>Loads the pictures and CSV tables an object's text asks to show (by attachment file name). Anything missing or too big is left out.</summary>
    public async Task<Dictionary<string, EmbedData>> LoadEmbedsAsync(ObjectId objectId, string? text, ObjectId? commentId = null)
    {
        var result = new Dictionary<string, EmbedData>(StringComparer.OrdinalIgnoreCase);
        var names = Markup.EmbedNames(text);
        if (names.Count == 0) return result;
        var attachments = await LoadAttachmentsAsync(objectId);
        foreach (var name in names)
        {
            // a comment's own files win over the object's files of the same name; object text only sees the object's files
            bool Named(AttachmentItem a) => string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase);
            var item = (commentId is { } owner ? attachments.LastOrDefault(a => a.CommentId == owner && Named(a)) : null) ?? attachments.LastOrDefault(a => a.CommentId is null && Named(a));
            if (item is null || AttachmentFile(item) is not { } file) continue;
            var info = new System.IO.FileInfo(file);
            if (Markup.ImageMime(name) is { } mime)
            {
                if (info.Length > Markup.MaxEmbedImageBytes) continue;
                result[name] = new EmbedData(true, mime, await System.IO.File.ReadAllBytesAsync(file), null);
            }
            else if (Markup.IsTableFile(name) && info.Length <= Markup.MaxEmbedTableBytes)
            {
                var rows = Csv.Parse(await System.IO.File.ReadAllTextAsync(file)).Take(Markup.MaxEmbedTableRows)
                    .Select(r => (IReadOnlyList<string>)r.Take(Markup.MaxEmbedTableColumns).ToList()).ToList();
                result[name] = new EmbedData(false, "text/csv", null, rows);
            }
        }
        return result;
    }

    // ---------- audit log ----------

    public sealed record AuditRow(string When, string Actor, string Action, string Detail);

    public async Task<List<AuditRow>> LoadAuditAsync(int limit = 1000) => (await Store.ListAuditAsync(limit))
        .Select(a => new AuditRow(a.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), a.Actor, a.Action, a.Detail.Replace('\n', ' '))).ToList();

    /// <summary>Text the Go to dialog searches: ID, text, module and every attribute value.</summary>
    public async Task<List<(ObjectId Id, string Text)>> LoadSearchTextAsync()
    {
        var objects = (await Store.ListObjectsAsync()).Where(o => o.ContainerId is not null).OrderBy(o => o.CanonicalId, NaturalOrder.Instance).ToList();
        var containers = (await Store.ListAllContainersAsync()).ToDictionary(c => c.Id, c => c.Name);
        var names = (await Store.ListAttributeDefinitionsAsync()).ToDictionary(a => a.Id, a => a.Name);
        var values = new Dictionary<ObjectId, List<string>>();
        foreach (var moduleId in objects.Select(o => o.ContainerId!.Value).Distinct())
            foreach (var v in await Store.ListAttributeValuesAsync(moduleId))
            {
                if (v.Value.Length == 0) continue;
                if (!values.TryGetValue(v.ObjectId, out var list)) { list = new List<string>(); values[v.ObjectId] = list; }
                list.Add($"{names.GetValueOrDefault(v.AttributeId, "?")}={v.Value}");
            }
        return objects.Select(o => (o.Id, $"{o.CanonicalId}  {Markup.Plain(o.Title)}   [{containers.GetValueOrDefault(o.ContainerId!.Value, "?")}]" +
            (values.TryGetValue(o.Id, out var vs) ? "   " + string.Join("; ", vs) : ""))).ToList();
    }

    // ---------- ReqIF ----------

    /// <summary>Writes the module and its child levels as a ReqIF file (links between the exported modules become relations).</summary>
    public async Task<int> ExportReqIfFileAsync(Container root, string path)
    {
        var modules = await ModuleAndDescendantsAsync(root);
        var ids = new HashSet<ObjectId>();
        var names = new List<string>();
        var exported = new List<ReqIfModule>();
        foreach (var module in modules)
        {
            var view = await LoadModuleAsync(module);
            foreach (var a in view.Attributes) if (!names.Contains(a.Name, StringComparer.OrdinalIgnoreCase)) names.Add(a.Name);
            exported.Add(new ReqIfModule(module.Name, view.Rows.Select(r =>
            {
                ids.Add(r.Id);
                return new ReqIfObject(r.Id.ToString(), r.CanonicalId, r.Title, r.IsHeading, r.Depth, view.Attributes.ToDictionary(a => a.Name, a => r[a.Id.ToString()], StringComparer.OrdinalIgnoreCase));
            }).ToList()));
        }
        var relations = (await Store.ListRelationshipsAsync()).Where(l => ids.Contains(l.SourceId) && ids.Contains(l.TargetId))
            .Select(l => new ReqIfRelation(l.SourceId.ToString(), l.TargetId.ToString(), l.Type.ToString())).ToList();
        var xml = ReqIf.Write(new ReqIfExport($"{root.Name} (TraceForge)", names, exported, relations), DateTimeOffset.UtcNow);
        await System.IO.File.WriteAllTextAsync(path, xml, new UTF8Encoding(false));
        return exported.Sum(m => m.Objects.Count);
    }

    // ---------- HTML report ----------

    /// <summary>Writes the module and its child levels as one self-contained HTML file.</summary>
    public async Task ExportHtmlFileAsync(Container root, string path)
    {
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        var relationships = await Store.ListRelationshipsAsync();
        var suspects = (await Store.ListSuspectLinksAsync(null)).SelectMany(s => new[] { s.SourceId, s.TargetId }).ToHashSet();
        string Ids(IEnumerable<ObjectId> ids) => string.Join(", ", ids.Select(i => objects.TryGetValue(i, out var o) ? o.CanonicalId : "?"));
        var modules = new List<ReportModule>();
        foreach (var module in await ModuleAndDescendantsAsync(root))
        {
            var view = await LoadModuleAsync(module);
            var embedsByObject = new Dictionary<ObjectId, Dictionary<string, EmbedData>>();
            foreach (var row in view.Rows.Where(r => r.Title.Contains("attach:", StringComparison.Ordinal))) embedsByObject[row.Id] = await LoadEmbedsAsync(row.Id, row.Title);
            var commentsByObject = new Dictionary<ObjectId, List<ReportComment>>();
            foreach (var row in view.Rows)
            {
                var comments = await Store.ListCommentsAsync(row.Id);
                if (comments.Count == 0) continue;
                var list = new List<ReportComment>();
                foreach (var thread in comments.Where(c => c.ParentId is null).OrderBy(c => c.CreatedUtc))
                    foreach (var c in new[] { thread }.Concat(comments.Where(x => x.ParentId == thread.Id).OrderBy(x => x.CreatedUtc)))
                        list.Add(new ReportComment(c.Author, c.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), c.Body, c.ParentId is not null, thread.Resolved, await LoadEmbedsAsync(row.Id, c.Body, c.Id)));
                commentsByObject[row.Id] = list;
            }
            var rows = view.Rows.Select(r => new ReportRow(r.Number, r.CanonicalId, r.Title, r.Depth, r.IsHeading,
                view.Attributes.Select(a => r[a.Id.ToString()]).ToList(),
                Ids(relationships.Where(l => l.TargetId == r.Id).Select(l => l.SourceId)), Ids(relationships.Where(l => l.SourceId == r.Id).Select(l => l.TargetId)), suspects.Contains(r.Id),
                embedsByObject.GetValueOrDefault(r.Id), commentsByObject.GetValueOrDefault(r.Id))).ToList();
            modules.Add(new ReportModule(module.Name, view.Attributes.Select(a => a.Name).ToList(), rows));
        }
        var html = HtmlReport.Render($"{root.Name} \u2014 requirements report", DateTime.Now.ToString("yyyy-MM-dd HH:mm"), modules);
        await System.IO.File.WriteAllTextAsync(path, html, new UTF8Encoding(false));
    }

    // ---------- coverage ----------

    public sealed record CoverageRow(string Module, int Total, string Downstream, string Upstream);

    /// <summary>Coverage of the module and every module below it (its child levels).</summary>
    public async Task<CoverageReport> LoadCoverageAsync(Container root)
    {
        var all = (await Store.ListContainersAsync()).Where(c => c.Kind == ContainerKind.Module).ToList();
        var modules = await ModuleAndDescendantsAsync(root);
        var parents = all.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value).ToHashSet();
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        var items = new List<CoverageObject>();
        foreach (var m in modules)
        {
            var entries = await Store.GetOutlineAsync(m.Id);
            var numbers = Outline.Number(entries);
            foreach (var e in entries.Where(e => !e.IsHeading).OrderBy(e => numbers.GetValueOrDefault(e.ObjectId, ""), NaturalOrder.Instance))
                if (objects.TryGetValue(e.ObjectId, out var o)) items.Add(new CoverageObject(o.Id, m.Id, numbers.GetValueOrDefault(o.Id, ""), o.CanonicalId, o.Title));
        }
        var coverageModules = modules.Select(m => new CoverageModule(m.Id, m.Name, all.Any(a => a.Id == m.ParentId), parents.Contains(m.Id))).ToList();
        return Coverage.Compute(coverageModules, items, await Store.ListRelationshipsAsync());
    }

    public static List<CoverageRow> SummaryRows(CoverageReport report) => report.Modules.Select(m => new CoverageRow(m.Name, m.Total,
        m.ExpectsDownstream ? $"{m.WithDownstream}/{m.Total} ({m.DownstreamPercent:0}%)" : "n/a (lowest level)",
        m.ExpectsUpstream ? $"{m.WithUpstream}/{m.Total} ({m.UpstreamPercent:0}%)" : "n/a (top level)")).ToList();

    public static async Task ExportCoverageFileAsync(CoverageReport report, string path)
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "Module", "Number", "Id", "Text", "Gap" } };
        rows.AddRange(report.Gaps.Select(g => (IReadOnlyList<string>)new[] { CsvGuard.Protect(g.ModuleName), g.Number, CsvGuard.Protect(g.CanonicalId), CsvGuard.Protect(g.Title), g.Description }));
        await System.IO.File.WriteAllTextAsync(path, Csv.Format(rows), Utf8Bom);
    }

    // ---------- history and baselines ----------

    public async Task<List<HistoryItem>> LoadHistoryAsync(ObjectId objectId) =>
        (await Store.ListRevisionsAsync(objectId))
            .Select(r => new HistoryItem(r.Revision, r.ChangedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), r.Actor, r.Summary, r.Title)).ToList();

    public Task<Baseline> CreateBaselineAsync(Container module, string name, string? note) => Store.CreateBaselineAsync(module.Id, name, note);
    public Task<IReadOnlyList<Baseline>> ListBaselinesAsync(Container module) => Store.ListBaselinesAsync(module.Id);

    private static string Describe(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Added", ChangeKind.Removed => "Removed", ChangeKind.Changed => "Changed", ChangeKind.Moved => "Moved",
        ChangeKind.LinkAdded => "Link added", ChangeKind.LinkRemoved => "Link removed", _ => kind.ToString(),
    };

    /// <summary>Compares a baseline with a newer baseline, or with the module's current state when <paramref name="newer"/> is null.</summary>
    public async Task<List<ChangeRow>> CompareAsync(Container module, Baseline older, Baseline? newer)
    {
        var from = (await Store.GetBaselineAsync(older.Id) ?? throw new InvalidOperationException("The baseline no longer exists.")).Content;
        var to = newer is null ? await Store.CaptureModuleAsync(module.Id)
            : (await Store.GetBaselineAsync(newer.Id) ?? throw new InvalidOperationException("The baseline no longer exists.")).Content;
        var attributeNames = (await Store.ListAttributeDefinitionsAsync()).ToDictionary(a => a.Id.ToString(), a => a.Name);
        var labels = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id, o => o.CanonicalId);
        var changes = Snapshots.Compare(from, to, k => attributeNames.GetValueOrDefault(k, k), id => labels.GetValueOrDefault(id, id.ToString()));
        return changes.Select(c => new ChangeRow(Describe(c.Kind), c.CanonicalId, c.Description, module.Name)).ToList();
    }

    // ---------- delete, copy, paste ----------

    /// <summary>Deletes the object and everything below it. The section stays restorable.</summary>
    public async Task<DeletedSection> DeleteAsync(Container module, ObjectId objectId)
    {
        var position = await PositionAsync(module.Id, objectId);
        var deleted = await Store.DeleteSectionAsync(module.Id, objectId);
        var batch = deleted.BatchId;
        Undo.Push(new UndoEntry($"Delete {deleted.CanonicalId}", module.Id, objectId,
            async () => { await Store.RestoreDeletedAsync(batch, position.Index); },
            async () => { batch = (await Store.DeleteSectionAsync(module.Id, objectId)).BatchId; }));
        return deleted;
    }

    public Task<IReadOnlyList<DeletedSection>> ListDeletedAsync(Container module) => Store.ListDeletedAsync(module.Id);

    public async Task<int> RestoreAsync(Container module, DeletedSection section)
    {
        var count = await Store.RestoreDeletedAsync(section.BatchId);
        await PushSectionCreatedAsync($"Restore {section.CanonicalId}", module, section.RootId);
        return count;
    }

    /// <summary>The in-app clipboard: a section copied with <see cref="CopyAsync"/>.</summary>
    public List<ClipItem>? Clipboard { get; private set; }

    /// <summary>Copies an object and everything below it (text, heading flags, attribute values; not links or IDs).</summary>
    public async Task<int> CopyAsync(Container module, ObjectId rootId)
    {
        var entries = await Store.GetOutlineAsync(module.Id);
        var numbers = Outline.Number(entries);
        var objects = (await Store.ListModuleObjectsAsync(module.Id)).ToDictionary(o => o.Id);
        var values = await ValuesByObjectAsync(module.Id);
        var children = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        var items = new List<ClipItem>();
        void Walk(OutlineEntry entry, int? parentIndex)
        {
            if (!objects.TryGetValue(entry.ObjectId, out var o)) return;
            var index = items.Count;
            items.Add(new ClipItem(parentIndex, o.Title, entry.IsHeading, numbers.GetValueOrDefault(o.Id, ""), values.GetValueOrDefault(o.Id) ?? new Dictionary<string, string>()));
            foreach (var child in children[entry.ObjectId].OrderBy(c => c.SortOrder).ThenBy(c => c.ObjectId.ToString(), StringComparer.Ordinal)) Walk(child, index);
        }
        var root = entries.FirstOrDefault(e => e.ObjectId == rootId) ?? throw new InvalidOperationException("The object is not in this module's outline.");
        Walk(root, null);
        Clipboard = items;
        return items.Count;
    }

    /// <summary>Tab-separated text of the clipboard section, for pasting into other programs.</summary>
    public string ClipboardText() => Clipboard is null ? "" : string.Join(Environment.NewLine, Depths(Clipboard).Select(x => $"{x.Item.Number}\t{new string(' ', x.Depth * 2)}{x.Item.Title}"));

    private static IEnumerable<(ClipItem Item, int Depth)> Depths(List<ClipItem> items)
    {
        var depth = new int[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            depth[i] = items[i].ParentIndex is { } p ? depth[p] + 1 : 0;
            yield return (items[i], depth[i]);
        }
    }

    /// <summary>Pastes the clipboard section as new objects (new IDs). Returns the new root id, object count and attribute values left out.</summary>
    public async Task<(ObjectId Root, int Count, int SkippedValues)> PasteAsync(Container module, ObjectId? selectedId, bool asChild)
    {
        var clip = Clipboard ?? throw new InvalidOperationException("Nothing has been copied yet. Select an object and use Copy first.");
        var (parent, index) = Target(await Store.GetOutlineAsync(module.Id), selectedId, asChild);
        var assigned = (await Store.ListModuleAttributesAsync(module.Id)).Select(a => a.Id.ToString()).ToHashSet();
        var ids = await NextCanonicalIdsAsync(module, clip.Count);
        var created = clip.Select((c, i) => EngineeringObject.Create(EngineeringObjectType.Requirement, ids[i], c.Title, module.Id)).ToList();
        var skipped = clip.Sum(c => c.Values.Keys.Count(k => !assigned.Contains(k)));
        var section = clip.Select((c, i) => new SectionItem(created[i], c.ParentIndex is { } p ? created[p].Id : null, c.IsHeading,
            c.Values.Where(kv => assigned.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value))).ToList();
        await Store.AddSectionAsync(module.Id, parent, index, section);
        await PushSectionCreatedAsync($"Paste {created.Count} object(s)", module, created[0].Id);
        return (created[0].Id, created.Count, skipped);
    }

    // ---------- baseline views and sets ----------

    public Task<IReadOnlyList<Baseline>> CreateBaselineSetAsync(Container module, string name, string? note) => Store.CreateBaselineSetAsync(module.Id, name, note);

    public async Task<List<Container>> ModuleAndDescendantsAsync(Container module)
    {
        var all = (await Store.ListContainersAsync()).Where(c => c.Kind == ContainerKind.Module).ToList();
        var result = new List<Container> { module };
        for (var i = 0; i < result.Count; i++) result.AddRange(InTreeOrder(all.Where(c => c.ParentId == result[i].Id)));
        return result;
    }

    public async Task<List<BaselineRow>> ViewBaselineAsync(Baseline baseline)
    {
        var snapshot = await Store.GetBaselineAsync(baseline.Id) ?? throw new InvalidOperationException("The baseline no longer exists.");
        var names = (await Store.ListAttributeDefinitionsAsync()).ToDictionary(a => a.Id.ToString(), a => a.Name);
        return snapshot.Content.Objects.OrderBy(o => o.Number.Length == 0 ? 1 : 0).ThenBy(o => o.Number, NaturalOrder.Instance).ThenBy(o => o.CanonicalId, NaturalOrder.Instance)
            .Select(o => new BaselineRow(o.Number, o.CanonicalId, o.Title,
                string.Join("; ", o.Values.OrderBy(v => names.GetValueOrDefault(v.Key, v.Key), StringComparer.OrdinalIgnoreCase).Select(v => $"{names.GetValueOrDefault(v.Key, v.Key)}: {v.Value}")))).ToList();
    }

    /// <summary>Compares every module of the set (the module and its descendants) that has a baseline of this name against its current state.</summary>
    public async Task<List<ChangeRow>> CompareSetAsync(Container root, string baselineName)
    {
        var rows = new List<ChangeRow>();
        foreach (var module in await ModuleAndDescendantsAsync(root))
        {
            var baseline = (await Store.ListBaselinesAsync(module.Id)).FirstOrDefault(b => string.Equals(b.Name, baselineName, StringComparison.OrdinalIgnoreCase));
            if (baseline is not null) rows.AddRange(await CompareAsync(module, baseline, null));
        }
        return rows;
    }

    public async Task<List<string>> BaselineNamesInSetAsync(Container root)
    {
        var names = new List<string>();
        foreach (var module in await ModuleAndDescendantsAsync(root))
            names.AddRange((await Store.ListBaselinesAsync(module.Id)).Select(b => b.Name));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, NaturalOrder.Instance).ToList();
    }

    // ---------- export and import (CSV) ----------

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The module as CSV: Number, ID, Heading, Text, one column per attribute, Links to, Linked from. Cells are protected against spreadsheet formulas.</summary>
    public async Task<string> ExportCsvAsync(Container module)
    {
        var view = await LoadModuleAsync(module);
        var relationships = await Store.ListRelationshipsAsync();
        var labels = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id, o => o.CanonicalId);
        string Label(ObjectId id) => labels.GetValueOrDefault(id, id.ToString());
        var header = new List<string> { "Number", "ID", "Heading", "Text" };
        header.AddRange(view.Attributes.Select(a => a.Name));
        header.Add("Links to"); header.Add("Linked from");
        var rows = new List<IReadOnlyList<string>> { header.Select(CsvGuard.Protect).ToList() };
        foreach (var r in view.Rows)
        {
            var cells = new List<string> { r.Number, r.CanonicalId, r.IsHeading ? "yes" : "", r.Title };
            cells.AddRange(view.Attributes.Select(a => r[a.Id.ToString()]));
            cells.Add(string.Join("; ", relationships.Where(x => x.SourceId == r.Id).OrderBy(x => x.CreatedUtc).Select(x => $"{x.Type}:{Label(x.TargetId)}")));
            cells.Add(string.Join("; ", relationships.Where(x => x.TargetId == r.Id).OrderBy(x => x.CreatedUtc).Select(x => $"{x.Type}:{Label(x.SourceId)}")));
            rows.Add(cells.Select(CsvGuard.Protect).ToList());
        }
        return Csv.Format(rows);
    }

    public async Task ExportCsvFileAsync(Container module, string path) => await System.IO.File.WriteAllTextAsync(path, await ExportCsvAsync(module), Utf8Bom);

    /// <summary>Writes one CSV file per module into the folder. Returns the file names written.</summary>
    public async Task<List<string>> ExportProjectAsync(string folder)
    {
        var written = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in (await Store.ListContainersAsync()).Where(c => c.Kind == ContainerKind.Module).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var safe = new string(module.Name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
            if (safe.Length == 0) safe = "module";
            var name = safe; var n = 2;
            while (!used.Add(name)) name = $"{safe} ({n++})";
            await ExportCsvFileAsync(module, System.IO.Path.Combine(folder, name + ".csv"));
            written.Add(name + ".csv");
        }
        return written;
    }

    /// <summary>Everything the import planner needs to know about the project and the target module.</summary>
    public async Task<ImportContext> BuildImportContextAsync(Container module)
    {
        var moduleAttributes = await Store.ListModuleAttributesAsync(module.Id);
        var assigned = moduleAttributes.Select(a => a.Id).ToHashSet();
        var other = (await Store.ListAttributeDefinitionsAsync()).Where(a => !assigned.Contains(a.Id)).ToList();
        var objects = await Store.ListObjectsAsync();
        var values = await ValuesByObjectAsync(module.Id);
        var existing = objects.Where(o => o.ContainerId == module.Id)
            .Select(o => new ExistingObject(o.Id, o.CanonicalId, o.Title, values.GetValueOrDefault(o.Id) ?? new Dictionary<string, string>())).ToList();
        var targets = new Dictionary<string, LinkTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects) targets[o.CanonicalId] = new LinkTarget(o.Id, o.ContainerId);
        var links = (await Store.ListRelationshipsAsync()).Select(r => (r.SourceId, r.TargetId, r.Type)).ToHashSet();
        return new ImportContext(moduleAttributes, other, existing, new HashSet<string>(await Store.ListAllCanonicalIdsAsync(), StringComparer.OrdinalIgnoreCase), targets,
            (await ChildModulesAsync(module)).Select(c => c.Id).ToHashSet(), links, false);
    }

    /// <summary>Applies a plan from <see cref="ImportPlanner"/> in one transaction and registers one undo step for the whole import.</summary>
    public async Task<ImportOutcome> ApplyImportAsync(Container module, ImportPlan plan)
    {
        var rows = plan.Rows;
        var created = new Dictionary<int, EngineeringObject>();
        var needIds = rows.Count(r => r.Action == ImportAction.Create && r.CanonicalId is null);
        var generated = await NextCanonicalIdsAsync(module, needIds, rows.Where(r => r.CanonicalId is not null).Select(r => r.CanonicalId!));
        var next = 0;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Action == ImportAction.Create)
                created[i] = EngineeringObject.Create(EngineeringObjectType.Requirement, rows[i].CanonicalId ?? generated[next++], rows[i].Text, module.Id);
        ObjectId? IdOf(int index) => created.TryGetValue(index, out var o) ? o.Id : rows[index].ExistingId;

        var items = created.OrderBy(kv => kv.Key).Select(kv => new SectionItem(kv.Value, rows[kv.Key].ParentRowIndex is { } p ? IdOf(p) : null, rows[kv.Key].IsHeading, rows[kv.Key].Values)).ToList();
        var updates = rows.Where(r => r.Action == ImportAction.Update).Select(r => new ObjectUpdate(r.ExistingId!.Value, r.Text, r.Values)).ToList();
        var links = plan.Links.Select(l => (Source: IdOf(l.RowIndex)!.Value, Target: l.TargetId, Type: l.Type)).ToList();
        var outcome = await Store.ImportAsync(module.Id, plan.NewAttributes, plan.AssignAttributes.Select(a => a.Id).ToList(), items, updates, links);

        // ----- one undo step for the whole import -----
        var positions = new Dictionary<ObjectId, int>();
        foreach (var id in outcome.Created) positions[id] = (await PositionAsync(module.Id, id)).Index;
        var afterRevisions = new Dictionary<ObjectId, long>();
        foreach (var (id, _) in outcome.Updated) afterRevisions[id] = await RevisionAsync(id);
        var linkIds = outcome.LinkIds.ToList();
        var batches = new List<(ObjectId Root, ObjectId Batch)>();
        Undo.Push(new UndoEntry($"Import into {module.Name}", module.Id, outcome.Created.FirstOrDefault(),
            async () =>
            {
                foreach (var id in linkIds) await Store.RemoveLinkAsync(id);
                batches.Clear();
                foreach (var id in outcome.Created)
                    if ((await Store.GetOutlineAsync(module.Id)).Any(e => e.ObjectId == id)) batches.Add((id, (await Store.DeleteSectionAsync(module.Id, id)).BatchId)); // children go with their parent
                foreach (var (id, previous) in outcome.Updated) await Store.RestoreRevisionAsync(id, previous);
            },
            async () =>
            {
                foreach (var (root, batch) in batches) await Store.RestoreDeletedAsync(batch, positions[root]);
                foreach (var (id, _) in outcome.Updated) await Store.RestoreRevisionAsync(id, afterRevisions[id]);
                linkIds.Clear();
                foreach (var l in links) linkIds.Add((await Store.AddLinkAsync(l.Source, l.Target, l.Type)).Id);
            }));
        return outcome;
    }

    // ---------- trace ----------

    /// <summary>Downstream trace for one object (its section plus links, across levels), or for every top-level entry of the module when root is null.</summary>
    public async Task<List<TraceItem>> LoadTraceAsync(Container module, ObjectId? root)
    {
        var objects = (await Store.ListObjectsAsync()).ToDictionary(o => o.Id);
        var containers = (await Store.ListAllContainersAsync()).ToDictionary(c => c.Id);
        var entries = new List<OutlineEntry>();
        var numbers = new Dictionary<ObjectId, string>();
        foreach (var m in containers.Values.Where(c => c.Kind == ContainerKind.Module))
        {
            var moduleEntries = await Store.GetOutlineAsync(m.Id);
            entries.AddRange(moduleEntries);
            foreach (var kv in Outline.Number(moduleEntries)) numbers[kv.Key] = kv.Value;
        }
        var sections = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        var links = (await Store.ListRelationshipsAsync()).ToLookup(r => r.SourceId);

        IEnumerable<ObjectId> Section(ObjectId id) => sections[id].OrderBy(e => e.SortOrder).ThenBy(e => e.ObjectId.ToString(), StringComparer.Ordinal).Select(e => e.ObjectId);
        IEnumerable<(ObjectId Target, string Label)> Links(ObjectId id) => links[id].OrderBy(r => r.CreatedUtc).Select(r => (r.TargetId, "→ " + r.Type));

        TraceItem ToItem(TraceNode node)
        {
            var text = objects.TryGetValue(node.ObjectId, out var o)
                ? $"{numbers.GetValueOrDefault(o.Id, "")} {o.CanonicalId}  {Markup.Plain(o.Title)}   [{(o.ContainerId is { } c && containers.TryGetValue(c, out var cm) ? cm.Name : "?")}]".Trim()
                : node.ObjectId.ToString();
            return new TraceItem(node.ObjectId, node.Kind == "root" ? "" : node.Kind, text, node.Children.Select(ToItem).ToList());
        }

        List<ObjectId> rootIds = root is { } rootId
            ? new List<ObjectId> { rootId }
            : entries.Where(e => e.ModuleId == module.Id && e.ParentId is null).OrderBy(e => e.SortOrder).Select(e => e.ObjectId).ToList();
        return rootIds.Select(id => ToItem(Tracing.Build(id, "root", Section, Links))).ToList();
    }

    public ValueTask DisposeAsync() => Store.DisposeAsync();
}
