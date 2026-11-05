using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;
using TraceForge.Infrastructure;
using Xunit;

namespace TraceForge.Infrastructure.Tests;

/// <summary>Modules, outline and attributes (schema v3).</summary>
public sealed class ModelingStoreTests : TempDirectoryTestBase
{
    private readonly ProjectPackageService _service = new();
    private string Project(string name) => Path.Combine(_root, name + ".traceproj");

    private sealed record Fixture(Container Module, EngineeringObject Heading, EngineeringObject Req1, EngineeringObject Req2);

    /// <summary>Module "SRS" with a heading and two requirements (not yet in the outline).</summary>
    private static async Task<Fixture> SeedAsync(IProjectStore store)
    {
        var module = Container.Create(ContainerKind.Module, "SRS");
        await store.AddContainerAsync(module);
        var h = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-000", "Introduction", module.Id);
        var r1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-001", "Boot time", module.Id);
        var r2 = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-002", "Logging", module.Id);
        await store.AddBatchAsync([h, r1, r2], []);
        return new(module, h, r1, r2);
    }

    [Fact] public async Task Outline_persists_with_derived_numbers_across_reopen()
    {
        var path = Project("a"); await _service.CreateAsync(path, "Outline");
        Fixture f;
        await using (var s = await _service.OpenAsync(path))
        {
            f = await SeedAsync(s);
            await s.AddToOutlineAsync(f.Module.Id, f.Heading.Id, null, true);
            await s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, f.Heading.Id, false);
            await s.AddToOutlineAsync(f.Module.Id, f.Req2.Id, f.Heading.Id, false);
        }
        await using var again = await _service.OpenAsync(path);
        var outline = await again.GetOutlineAsync(f.Module.Id);
        var numbers = Outline.Number(outline);
        Assert.Equal("1", numbers[f.Heading.Id]); Assert.Equal("1.1", numbers[f.Req1.Id]); Assert.Equal("1.2", numbers[f.Req2.Id]);
        Assert.True(outline.Single(e => e.ObjectId == f.Heading.Id).IsHeading);
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Move_is_persisted_and_a_move_under_a_descendant_changes_nothing()
    {
        var path = Project("b"); await _service.CreateAsync(path, "Move");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        await s.AddToOutlineAsync(f.Module.Id, f.Heading.Id, null, true);
        await s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, f.Heading.Id, false);
        await s.AddToOutlineAsync(f.Module.Id, f.Req2.Id, f.Heading.Id, false);

        await s.MoveOutlineEntryAsync(f.Module.Id, f.Req2.Id, f.Heading.Id, 0); // Logging before Boot time
        var n = Outline.Number(await s.GetOutlineAsync(f.Module.Id));
        Assert.Equal("1.1", n[f.Req2.Id]); Assert.Equal("1.2", n[f.Req1.Id]);

        var before = await s.GetOutlineAsync(f.Module.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveOutlineEntryAsync(f.Module.Id, f.Heading.Id, f.Req1.Id, 0));
        Assert.Equal(before.OrderBy(e => e.ObjectId.ToString()), (await s.GetOutlineAsync(f.Module.Id)).OrderBy(e => e.ObjectId.ToString()));
    }

    [Fact] public async Task Outline_rejects_foreign_objects_unknown_parents_non_modules_and_duplicates()
    {
        var path = Project("c"); await _service.CreateAsync(path, "Rules");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var other = Container.Create(ContainerKind.Module, "SDD"); await s.AddContainerAsync(other);
        var folder = Container.Create(ContainerKind.Folder, "Misc"); await s.AddContainerAsync(folder);
        var elsewhere = EngineeringObject.Create(EngineeringObjectType.Requirement, "SDD-001", "Elsewhere", other.Id);
        await s.AddObjectAsync(elsewhere);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddToOutlineAsync(f.Module.Id, elsewhere.Id, null, false)); // object belongs to another module
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, f.Req2.Id, false)); // parent not in outline yet
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddToOutlineAsync(folder.Id, f.Req1.Id, null, false)); // folders are not modules
        await s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, null, false);
        await Assert.ThrowsAsync<SqliteException>(() => s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, null, false)); // already placed
        Assert.Single(await s.GetOutlineAsync(f.Module.Id));
    }

    [Fact] public async Task Attributes_are_typed_validated_assigned_and_persisted()
    {
        var path = Project("d"); await _service.CreateAsync(path, "Attributes");
        Fixture f; AttributeDefinition status, priority;
        await using (var s = await _service.OpenAsync(path))
        {
            f = await SeedAsync(s);
            status = AttributeDefinition.Create("Status", AttributeDataType.Enumeration, isRequired: true, enumValues: ["Draft", "Approved"]);
            priority = AttributeDefinition.Create("Priority", AttributeDataType.Integer);
            await s.AddAttributeDefinitionAsync(status); await s.AddAttributeDefinitionAsync(priority);
            await Assert.ThrowsAsync<SqliteException>(() => s.AddAttributeDefinitionAsync(AttributeDefinition.Create("status", AttributeDataType.Text))); // names are unique, case-insensitive

            await Assert.ThrowsAsync<InvalidOperationException>(() => s.SetAttributeValueAsync(f.Req1.Id, status.Id, "Draft")); // not assigned to the module yet
            await s.AssignAttributeToModuleAsync(f.Module.Id, status.Id); await s.AssignAttributeToModuleAsync(f.Module.Id, priority.Id);

            await s.SetAttributeValueAsync(f.Req1.Id, status.Id, "approved");   // canonicalised
            await s.SetAttributeValueAsync(f.Req1.Id, priority.Id, " 7 ");
            await Assert.ThrowsAsync<ArgumentException>(() => s.SetAttributeValueAsync(f.Req1.Id, priority.Id, "high"));
            await Assert.ThrowsAsync<ArgumentException>(() => s.SetAttributeValueAsync(f.Req1.Id, status.Id, "Done"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.SetAttributeValueAsync(f.Req1.Id, status.Id, ""));   // required: cannot clear
            await s.SetAttributeValueAsync(f.Req2.Id, priority.Id, "3");
            await s.SetAttributeValueAsync(f.Req2.Id, priority.Id, null);         // optional: clears
        }
        await using var again = await _service.OpenAsync(path);
        Assert.Equal(new[] { "Status", "Priority" }, (await again.ListModuleAttributesAsync(f.Module.Id)).Select(a => a.Name));
        var defs = await again.ListAttributeDefinitionsAsync();
        var storedStatus = defs.Single(d => d.Name == "Status");
        Assert.Equal(AttributeDataType.Enumeration, storedStatus.DataType); Assert.True(storedStatus.IsRequired); Assert.Equal(new[] { "Draft", "Approved" }, storedStatus.EnumValues);
        var values = await again.ListAttributeValuesAsync(f.Module.Id);
        Assert.Equal(2, values.Count);
        Assert.Contains(values, v => v.ObjectId == f.Req1.Id && v.AttributeId == status.Id && v.Value == "Approved");
        Assert.Contains(values, v => v.ObjectId == f.Req1.Id && v.AttributeId == priority.Id && v.Value == "7");
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Missing_required_values_are_reported_per_object()
    {
        var path = Project("e"); await _service.CreateAsync(path, "Missing");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var owner = AttributeDefinition.Create("Owner", AttributeDataType.Text, isRequired: true);
        await s.AddAttributeDefinitionAsync(owner); await s.AssignAttributeToModuleAsync(f.Module.Id, owner.Id);
        await s.SetAttributeValueAsync(f.Req1.Id, owner.Id, "Pat");
        var missing = await s.FindMissingRequiredValuesAsync(f.Module.Id);
        Assert.Equal(new[] { f.Heading.Id, f.Req2.Id }, missing.Select(m => m.ObjectId)); // SRS-000, SRS-002
        Assert.All(missing, m => Assert.Equal(owner.Id, m.AttributeId));
    }

    [Fact] public async Task Integrity_check_detects_outline_cycles_and_invalid_attribute_values()
    {
        var path = Project("f"); await _service.CreateAsync(path, "Corrupt");
        Fixture f; AttributeDefinition priority;
        await using (var s = await _service.OpenAsync(path))
        {
            f = await SeedAsync(s);
            priority = AttributeDefinition.Create("Priority", AttributeDataType.Integer);
            await s.AddAttributeDefinitionAsync(priority); await s.AssignAttributeToModuleAsync(f.Module.Id, priority.Id);
            await s.SetAttributeValueAsync(f.Req1.Id, priority.Id, "5");
            await s.AddToOutlineAsync(f.Module.Id, f.Heading.Id, null, true);
            await s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, f.Heading.Id, false);
        }
        Assert.True((await _service.CheckAsync(path)).IsValid);
        await RawAsync(Path.Combine(path, "project.db"),
            $"UPDATE outline_entries SET parent_object_id='{f.Req1.Id}' WHERE object_id='{f.Heading.Id}'", // heading under its own child
            $"UPDATE attribute_values SET value='high' WHERE object_id='{f.Req1.Id}'");
        var report = await _service.CheckAsync(path);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, x => x.StartsWith("[outline]") && x.Contains("cycle"));
        Assert.Contains(report.Findings, x => x.StartsWith("[attribute]") && x.Contains("Priority"));
    }

    [Fact] public async Task Integrity_check_detects_outline_entries_in_the_wrong_module()
    {
        var path = Project("g"); await _service.CreateAsync(path, "Wrong module");
        Fixture f; Container other;
        await using (var s = await _service.OpenAsync(path))
        {
            f = await SeedAsync(s);
            other = Container.Create(ContainerKind.Module, "SDD"); await s.AddContainerAsync(other);
            await s.AddToOutlineAsync(f.Module.Id, f.Req1.Id, null, false);
        }
        await RawAsync(Path.Combine(path, "project.db"), $"UPDATE engineering_objects SET container_id='{other.Id}' WHERE id='{f.Req1.Id}'");
        var report = await _service.CheckAsync(path);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, x => x.StartsWith("[outline]") && x.Contains("not in the entry's module"));
    }
}

/// <summary>Module levels and parent-to-child links.</summary>
public sealed class LinkStoreTests : TempDirectoryTestBase
{
    [Fact] public async Task Links_must_go_from_a_module_to_its_direct_child_module()
    {
        var service = new ProjectPackageService(); var path = Path.Combine(_root, "l.traceproj"); await service.CreateAsync(path, "Levels");
        await using var s = await service.OpenAsync(path);
        var needs = Container.Create(ContainerKind.Module, "Needs");
        var system = Container.Create(ContainerKind.Module, "System", needs.Id);
        var sub = Container.Create(ContainerKind.Module, "Subsystem", system.Id);
        var sibling = Container.Create(ContainerKind.Module, "Other");
        foreach (var c in new[] { needs, system, sub, sibling }) await s.AddContainerAsync(c);
        EngineeringObject Obj(string id, Container m) => EngineeringObject.Create(EngineeringObjectType.Requirement, id, id, m.Id);
        EngineeringObject n1 = Obj("N-1", needs), s1 = Obj("S-1", system), s2 = Obj("S-2", system), u1 = Obj("U-1", sub), o1 = Obj("O-1", sibling);
        await s.AddBatchAsync([n1, s1, s2, u1, o1], []);

        var link = await s.AddLinkAsync(n1.Id, s1.Id, RelationshipType.Decomposes);   // parent -> child level: allowed
        await s.AddLinkAsync(n1.Id, s2.Id, RelationshipType.Decomposes);              // one parent, several children
        await s.AddLinkAsync(s1.Id, u1.Id, RelationshipType.Decomposes);              // next level down
        Assert.Equal(3, (await s.ListRelationshipsAsync()).Count);
        Assert.Contains(await s.ListRelationshipsAsync(), r => r.Id == link.Id && r.SourceId == n1.Id && r.TargetId == s1.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddLinkAsync(n1.Id, u1.Id, RelationshipType.Decomposes)); // skips a level
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddLinkAsync(s1.Id, n1.Id, RelationshipType.Decomposes)); // child -> parent
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddLinkAsync(s1.Id, s2.Id, RelationshipType.Decomposes)); // same module
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddLinkAsync(n1.Id, o1.Id, RelationshipType.Decomposes)); // unrelated module
        await Assert.ThrowsAsync<SqliteException>(() => s.AddLinkAsync(n1.Id, s1.Id, RelationshipType.Decomposes));            // duplicate
        Assert.Equal(3, (await s.ListRelationshipsAsync()).Count);
        Assert.True((await service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Module_levels_persist_as_a_container_tree()
    {
        var service = new ProjectPackageService(); var path = Path.Combine(_root, "t.traceproj"); await service.CreateAsync(path, "Tree");
        var a = Container.Create(ContainerKind.Module, "A"); var b = Container.Create(ContainerKind.Module, "B", a.Id);
        await using (var s = await service.OpenAsync(path)) { await s.AddContainerAsync(a); await s.AddContainerAsync(b); }
        await using var again = await service.OpenAsync(path);
        Assert.Equal(a.Id, (await again.ListContainersAsync()).Single(c => c.Name == "B").ParentId);
    }
}

/// <summary>Saved views and link removal (schema v4).</summary>
public sealed class ViewStoreTests : TempDirectoryTestBase
{
    [Fact] public async Task Views_are_saved_replaced_by_name_listed_and_deleted()
    {
        var service = new ProjectPackageService(); var path = Path.Combine(_root, "v.traceproj"); await service.CreateAsync(path, "Views");
        var module = Container.Create(ContainerKind.Module, "SRS");
        await using (var s = await service.OpenAsync(path))
        {
            await s.AddContainerAsync(module);
            var first = new SavedView(ObjectId.New(), module.Id, "Open items", new ViewDefinition("todo", null, "", LinkFilter.NoOutLinks, ["a1"]), DateTimeOffset.UtcNow);
            await s.SaveViewAsync(first);
            await s.SaveViewAsync(new SavedView(ObjectId.New(), module.Id, "open ITEMS", new ViewDefinition("changed", null, "", LinkFilter.Any, null), DateTimeOffset.UtcNow)); // same name, other case: replaces
            await s.SaveViewAsync(new SavedView(ObjectId.New(), module.Id, "Another", ViewDefinition.Default, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<ArgumentException>(() => s.SaveViewAsync(new SavedView(ObjectId.New(), module.Id, " ", ViewDefinition.Default, DateTimeOffset.UtcNow)));
        }
        await using var again = await service.OpenAsync(path);
        var views = await again.ListViewsAsync(module.Id);
        Assert.Equal(new[] { "Another", "open ITEMS" }, views.Select(v => v.Name));
        Assert.Equal("changed", views.Single(v => v.Name == "open ITEMS").Definition.Text);
        await again.DeleteViewAsync(views[0].Id);
        Assert.Single(await again.ListViewsAsync(module.Id));
    }

    [Fact] public async Task Link_removal_is_audited_and_unknown_links_are_rejected()
    {
        var service = new ProjectPackageService(); var path = Path.Combine(_root, "r.traceproj"); await service.CreateAsync(path, "Remove");
        await using var s = await service.OpenAsync(path);
        var top = Container.Create(ContainerKind.Module, "Top"); var low = Container.Create(ContainerKind.Module, "Low", top.Id);
        await s.AddContainerAsync(top); await s.AddContainerAsync(low);
        var a = EngineeringObject.Create(EngineeringObjectType.Requirement, "T-1", "a", top.Id); var b = EngineeringObject.Create(EngineeringObjectType.Requirement, "L-1", "b", low.Id);
        await s.AddBatchAsync([a, b], []);
        var link = await s.AddLinkAsync(a.Id, b.Id, RelationshipType.Constrains);
        await s.RemoveLinkAsync(link.Id);
        Assert.Empty(await s.ListRelationshipsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RemoveLinkAsync(link.Id));
        await s.AddLinkAsync(a.Id, b.Id, RelationshipType.Constrains); // can be re-created after removal
        Assert.Single(await s.ListRelationshipsAsync());
    }
}

/// <summary>Object history and baselines (schema v5).</summary>
public sealed class HistoryStoreTests : TempDirectoryTestBase
{
    private readonly ProjectPackageService _service = new();

    private sealed record Seed(Container Module, Container Child, EngineeringObject Req, EngineeringObject ChildReq, AttributeDefinition Priority);

    private static async Task<Seed> SeedAsync(IProjectStore s)
    {
        var module = Container.Create(ContainerKind.Module, "SRS");
        var child = Container.Create(ContainerKind.Module, "SDD", module.Id);
        await s.AddContainerAsync(module); await s.AddContainerAsync(child);
        var req = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-001", "Boot time", module.Id);
        var childReq = EngineeringObject.Create(EngineeringObjectType.Requirement, "SDD-001", "Fast loader", child.Id);
        await s.AddBatchAsync([req, childReq], []);
        await s.AddToOutlineAsync(module.Id, req.Id, null, false);
        await s.AddToOutlineAsync(child.Id, childReq.Id, null, false);
        var priority = AttributeDefinition.Create("Priority", AttributeDataType.Enumeration, false, new[] { "low", "high" });
        await s.AddAttributeDefinitionAsync(priority);
        await s.AssignAttributeToModuleAsync(module.Id, priority.Id);
        return new(module, child, req, childReq, priority);
    }

    [Fact] public async Task Every_change_adds_an_immutable_revision_and_unchanged_values_add_none()
    {
        var path = Path.Combine(_root, "h.traceproj"); await _service.CreateAsync(path, "History");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        await s.SetAttributeValueAsync(f.Req.Id, f.Priority.Id, "low");
        await s.SetAttributeValueAsync(f.Req.Id, f.Priority.Id, "low");   // unchanged
        await s.SetAttributeValueAsync(f.Req.Id, f.Priority.Id, "HIGH");  // canonical spelling
        await s.RenameObjectAsync(f.Req.Id, "  Boot time under 2 s ");
        await s.RenameObjectAsync(f.Req.Id, "Boot time under 2 s");       // unchanged
        var revisions = await s.ListRevisionsAsync(f.Req.Id);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, revisions.Select(r => r.Revision));
        Assert.Equal("Boot time under 2 s", revisions[0].Title);
        Assert.Equal("high", revisions[0].Values[f.Priority.Id.ToString()]);
        Assert.Equal("low", revisions[2].Values[f.Priority.Id.ToString()]);
        Assert.Equal("Boot time", revisions[3].Title); Assert.Empty(revisions[3].Values);
        Assert.Equal(4, (await s.ListObjectsAsync()).Single(o => o.Id == f.Req.Id).Revision);
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Revisions_are_append_only_and_blank_text_is_rejected()
    {
        var path = Path.Combine(_root, "a.traceproj"); await _service.CreateAsync(path, "Append");
        Seed f;
        await using (var s = await _service.OpenAsync(path))
        {
            f = await SeedAsync(s);
            await Assert.ThrowsAsync<ArgumentException>(() => s.RenameObjectAsync(f.Req.Id, "  "));
        }
        await using var raw = new SqliteConnection($"Data Source={Path.Combine(path, "project.db")};Pooling=False");
        await raw.OpenAsync();
        foreach (var sql in new[] { "UPDATE object_revisions SET title='x'", "DELETE FROM object_revisions" })
        {
            await using var cmd = raw.CreateCommand(); cmd.CommandText = sql;
            await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        }
    }

    [Fact] public async Task Baseline_freezes_content_and_comparison_with_current_shows_later_changes()
    {
        var path = Path.Combine(_root, "b.traceproj"); await _service.CreateAsync(path, "Baselines");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        await s.SetAttributeValueAsync(f.Req.Id, f.Priority.Id, "low");
        await s.AddLinkAsync(f.Req.Id, f.ChildReq.Id, RelationshipType.Decomposes);
        var baseline = await s.CreateBaselineAsync(f.Module.Id, "Release 1", "first freeze");

        await s.SetAttributeValueAsync(f.Req.Id, f.Priority.Id, "high");
        await s.RenameObjectAsync(f.Req.Id, "Boot time under 2 s");
        var added = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-002", "Logging", f.Module.Id);
        await s.AddObjectAsync(added); await s.AddToOutlineAsync(f.Module.Id, added.Id, null, false);

        var frozen = (await s.GetBaselineAsync(baseline.Id))!;
        Assert.Equal("first freeze", frozen.Baseline.Note);
        var obj = Assert.Single(frozen.Content.Objects);
        Assert.Equal("Boot time", obj.Title); Assert.Equal("1", obj.Number); Assert.Equal("low", obj.Values[f.Priority.Id.ToString()]);
        Assert.Single(frozen.Content.Links);

        var changes = Snapshots.Compare(frozen.Content, await s.CaptureModuleAsync(f.Module.Id), k => k == f.Priority.Id.ToString() ? "Priority" : k, id => id.ToString());
        Assert.Contains(changes, c => c.Kind == ChangeKind.Added && c.CanonicalId == "SRS-002");
        Assert.Contains(changes, c => c.Kind == ChangeKind.Changed && c.CanonicalId == "SRS-001" && c.Description.Contains("Priority: 'low' → 'high'"));
        Assert.DoesNotContain(changes, c => c.Kind is ChangeKind.LinkAdded or ChangeKind.LinkRemoved);
    }

    [Fact] public async Task Baseline_names_are_unique_per_module_blank_names_rejected_and_baselines_are_frozen()
    {
        var path = Path.Combine(_root, "n.traceproj"); await _service.CreateAsync(path, "Names");
        Seed f;
        await using (var s = await _service.OpenAsync(path))
        {
            f = await SeedAsync(s);
            await s.AddLinkAsync(f.Req.Id, f.ChildReq.Id, RelationshipType.Decomposes); // so baseline_links has a row for the trigger check
            await s.CreateBaselineAsync(f.Module.Id, "R1", null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBaselineAsync(f.Module.Id, "r1", null));
            await Assert.ThrowsAsync<ArgumentException>(() => s.CreateBaselineAsync(f.Module.Id, " ", null));
            await s.CreateBaselineAsync(f.Child.Id, "R1", null); // same name, other module
            Assert.Single(await s.ListBaselinesAsync(f.Module.Id));
        }
        await using var raw = new SqliteConnection($"Data Source={Path.Combine(path, "project.db")};Pooling=False");
        await raw.OpenAsync();
        foreach (var sql in new[] { "UPDATE baselines SET name='x'", "DELETE FROM baselines", "DELETE FROM baseline_objects", "UPDATE baseline_objects SET title='x'", "DELETE FROM baseline_links" })
        {
            await using var cmd = raw.CreateCommand(); cmd.CommandText = sql;
            await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        }
    }
}

/// <summary>Delete/restore, paste and baseline sets (schema v6).</summary>
public sealed class SectionStoreTests : TempDirectoryTestBase
{
    private readonly ProjectPackageService _service = new();

    private sealed record Tree(Container Module, Container Child, EngineeringObject A, EngineeringObject A1, EngineeringObject A2, EngineeringObject B, EngineeringObject ChildReq, AttributeDefinition Required);

    /// <summary>Outline: A (A1, A2), B. Child module holds one object linked from A1. A required attribute is set on every SRS object.</summary>
    private static async Task<Tree> SeedAsync(IProjectStore s)
    {
        var module = Container.Create(ContainerKind.Module, "SRS");
        var child = Container.Create(ContainerKind.Module, "SDD", module.Id);
        await s.AddContainerAsync(module); await s.AddContainerAsync(child);
        EngineeringObject O(string id, string title, Container c) => EngineeringObject.Create(EngineeringObjectType.Requirement, id, title, c.Id);
        var a = O("SRS-001", "A", module); var a1 = O("SRS-002", "A1", module); var a2 = O("SRS-003", "A2", module); var b = O("SRS-004", "B", module);
        var childReq = O("SDD-001", "Loader", child);
        await s.AddBatchAsync([a, a1, a2, b, childReq], []);
        await s.AddToOutlineAsync(module.Id, a.Id, null, true);
        await s.AddToOutlineAsync(module.Id, a1.Id, a.Id, false);
        await s.AddToOutlineAsync(module.Id, a2.Id, a.Id, false);
        await s.AddToOutlineAsync(module.Id, b.Id, null, false);
        await s.AddToOutlineAsync(child.Id, childReq.Id, null, false);
        var required = AttributeDefinition.Create("Owner", AttributeDataType.Text, true, Array.Empty<string>());
        await s.AddAttributeDefinitionAsync(required);
        await s.AssignAttributeToModuleAsync(module.Id, required.Id);
        foreach (var o in new[] { a, a1, a2, b }) await s.SetAttributeValueAsync(o.Id, required.Id, "team-" + o.Title);
        await s.AddLinkAsync(a1.Id, childReq.Id, RelationshipType.Decomposes);
        return new(module, child, a, a1, a2, b, childReq, required);
    }

    [Fact] public async Task Deleting_a_section_hides_it_everywhere_and_restore_brings_it_back_at_the_end()
    {
        var path = Path.Combine(_root, "d.traceproj"); await _service.CreateAsync(path, "Delete");
        await using var s = await _service.OpenAsync(path);
        var t = await SeedAsync(s);

        Assert.Equal(3, (await s.DeleteSectionAsync(t.Module.Id, t.A.Id)).Count);
        Assert.Equal(new[] { t.B.Id }, (await s.GetOutlineAsync(t.Module.Id)).Select(e => e.ObjectId));
        Assert.DoesNotContain(await s.ListObjectsAsync(), o => o.Id == t.A1.Id);
        Assert.Empty(await s.ListRelationshipsAsync());                               // the link from A1 is hidden
        Assert.Empty(await s.FindMissingRequiredValuesAsync(t.Module.Id));
        Assert.DoesNotContain((await s.CaptureModuleAsync(t.Module.Id)).Objects, o => o.CanonicalId is "SRS-001" or "SRS-002" or "SRS-003");
        Assert.Contains("SRS-001", await s.ListAllCanonicalIdsAsync());               // IDs are never reused
        Assert.True((await _service.CheckAsync(path)).IsValid);

        var deleted = Assert.Single(await s.ListDeletedAsync(t.Module.Id));
        Assert.Equal(t.A.Id, deleted.RootId); Assert.Equal(3, deleted.Count);

        Assert.Equal(3, await s.RestoreDeletedAsync(deleted.BatchId));
        var numbers = Outline.Number(await s.GetOutlineAsync(t.Module.Id));
        Assert.Equal("1", numbers[t.B.Id]); Assert.Equal("2", numbers[t.A.Id]);       // restored as the last top-level entry
        Assert.Equal("2.1", numbers[t.A1.Id]); Assert.Equal("2.2", numbers[t.A2.Id]);
        Assert.Single(await s.ListRelationshipsAsync());
        Assert.Empty(await s.ListDeletedAsync(t.Module.Id));
        Assert.True((await _service.CheckAsync(path)).IsValid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RestoreDeletedAsync(deleted.BatchId)); // already restored
    }

    [Fact] public async Task A_child_section_cannot_be_restored_under_a_deleted_parent()
    {
        var path = Path.Combine(_root, "p.traceproj"); await _service.CreateAsync(path, "Parent");
        await using var s = await _service.OpenAsync(path);
        var t = await SeedAsync(s);
        await s.DeleteSectionAsync(t.Module.Id, t.A1.Id);
        await s.DeleteSectionAsync(t.Module.Id, t.A.Id);                              // A now has only A2 left in its batch
        var batches = await s.ListDeletedAsync(t.Module.Id);
        var childBatch = batches.Single(b => b.RootId == t.A1.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RestoreDeletedAsync(childBatch.BatchId));
        await s.RestoreDeletedAsync(batches.Single(b => b.RootId == t.A.Id).BatchId);
        await s.RestoreDeletedAsync(childBatch.BatchId);                              // fine once the parent is back
        Assert.Equal(4, (await s.GetOutlineAsync(t.Module.Id)).Count);
    }

    [Fact] public async Task Deleted_objects_leave_baselines_and_unknown_sections_are_rejected()
    {
        var path = Path.Combine(_root, "b.traceproj"); await _service.CreateAsync(path, "Baseline");
        await using var s = await _service.OpenAsync(path);
        var t = await SeedAsync(s);
        await s.DeleteSectionAsync(t.Module.Id, t.B.Id);
        var baseline = await s.CreateBaselineAsync(t.Module.Id, "R1", null);
        Assert.DoesNotContain((await s.GetBaselineAsync(baseline.Id))!.Content.Objects, o => o.CanonicalId == "SRS-004");
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteSectionAsync(t.Module.Id, t.B.Id)); // already deleted: not in the live outline
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteSectionAsync(t.Child.Id, t.A.Id));  // wrong module
    }

    [Fact] public async Task Pasting_a_section_is_atomic_keeps_structure_and_values_and_honours_the_index()
    {
        var path = Path.Combine(_root, "c.traceproj"); await _service.CreateAsync(path, "Paste");
        await using var s = await _service.OpenAsync(path);
        var t = await SeedAsync(s);
        EngineeringObject N(string id, string title) => EngineeringObject.Create(EngineeringObjectType.Requirement, id, title, t.Module.Id);
        var root = N("SRS-010", "Copy of A"); var kid = N("SRS-011", "Copy of A1");
        var other = AttributeDefinition.Create("Unassigned", AttributeDataType.Text, false, Array.Empty<string>());
        await s.AddAttributeDefinitionAsync(other);
        await s.AddSectionAsync(t.Module.Id, null, 0, new[]
        {
            new SectionItem(root, null, true, new Dictionary<string, string> { [t.Required.Id.ToString()] = "copied-team", [other.Id.ToString()] = "dropped" }),
            new SectionItem(kid, root.Id, false, new Dictionary<string, string>()),
        });
        var numbers = Outline.Number(await s.GetOutlineAsync(t.Module.Id));
        Assert.Equal("1", numbers[root.Id]); Assert.Equal("1.1", numbers[kid.Id]); Assert.Equal("2", numbers[t.A.Id]); // index 0: before A
        var values = await s.ListAttributeValuesAsync(t.Module.Id);
        Assert.Equal("copied-team", values.Single(v => v.ObjectId == root.Id).Value);
        Assert.DoesNotContain(values, v => v.AttributeId == other.Id);                 // not assigned to the module: left out
        Assert.Equal("Created", (await s.ListRevisionsAsync(root.Id)).Single().Summary);
        Assert.True((await _service.CheckAsync(path)).IsValid);

        // Atomic: a bad second item (duplicate canonical ID) leaves nothing behind.
        var before = (await s.ListObjectsAsync()).Count;
        var good = N("SRS-020", "Good"); var dup = N("SRS-001", "Duplicate ID");
        await Assert.ThrowsAnyAsync<Exception>(() => s.AddSectionAsync(t.Module.Id, null, null, new[]
        {
            new SectionItem(good, null, false, new Dictionary<string, string>()), new SectionItem(dup, null, false, new Dictionary<string, string>()),
        }));
        Assert.Equal(before, (await s.ListObjectsAsync()).Count);
        // Wrong module and parent-before-child violations are rejected.
        var foreign = EngineeringObject.Create(EngineeringObjectType.Requirement, "SDD-050", "Foreign", t.Child.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddSectionAsync(t.Module.Id, null, null, new[] { new SectionItem(foreign, null, false, new Dictionary<string, string>()) }));
    }

    [Fact] public async Task Baseline_set_freezes_a_module_and_all_modules_below_it_together_or_not_at_all()
    {
        var path = Path.Combine(_root, "s.traceproj"); await _service.CreateAsync(path, "Set");
        await using var s = await _service.OpenAsync(path);
        var t = await SeedAsync(s);
        var set = await s.CreateBaselineSetAsync(t.Module.Id, "Release 1", "both levels");
        Assert.Equal(2, set.Count);
        Assert.Single(await s.ListBaselinesAsync(t.Module.Id)); Assert.Single(await s.ListBaselinesAsync(t.Child.Id));
        await s.CreateBaselineAsync(t.Child.Id, "Only child", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBaselineSetAsync(t.Module.Id, "Only child", null));   // child already has it
        Assert.Single(await s.ListBaselinesAsync(t.Module.Id));                       // nothing partial was kept for the parent
    }
}
