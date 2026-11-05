using TraceForge.Application;
using TraceForge.Domain;
using TraceForge.Infrastructure;
using Xunit;

namespace TraceForge.Infrastructure.Tests;

/// <summary>Rename, reorder, re-parent and delete of folders and modules, and removing attribute columns (no schema change).</summary>
public sealed class ContainerStoreTests : TempDirectoryTestBase
{
    private readonly ProjectPackageService _service = new();

    private async Task<IProjectStore> OpenAsync(string name)
    {
        var path = Path.Combine(_root, name + ".traceproj");
        await _service.CreateAsync(path, name);
        return await _service.OpenAsync(path);
    }

    private static async Task<Container> AddAsync(IProjectStore s, ContainerKind kind, string name, ObjectId? parent = null)
    {
        var c = Container.Create(kind, name, parent);
        await s.AddContainerAsync(c);
        return c;
    }

    private static async Task<List<string>> ChildNamesAsync(IProjectStore s, ObjectId? parent) =>
        (await s.ListContainersAsync()).Where(c => c.ParentId == parent).OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.Name).ToList();

    [Fact] public async Task New_containers_are_appended_after_existing_siblings()
    {
        await using var s = await OpenAsync("append");
        await AddAsync(s, ContainerKind.Module, "Zebra"); await AddAsync(s, ContainerKind.Module, "Apple"); await AddAsync(s, ContainerKind.Module, "Mango");
        Assert.Equal(new[] {"Zebra", "Apple", "Mango"}, await ChildNamesAsync(s, null));
    }

    [Fact] public async Task Rename_changes_the_name_and_is_audited_and_rejects_blank_names()
    {
        await using var s = await OpenAsync("rename");
        var m = await AddAsync(s, ContainerKind.Module, "SRS");
        await s.RenameContainerAsync(m.Id, "  System Requirements ");
        Assert.Equal("System Requirements", Assert.Single(await s.ListContainersAsync()).Name);
        Assert.Contains(await s.ListAuditAsync(50), a => a.Action == "container.renamed");
        await Assert.ThrowsAsync<ArgumentException>(() => s.RenameContainerAsync(m.Id, "  "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RenameContainerAsync(ObjectId.New(), "x"));
    }

    [Fact] public async Task Move_reorders_among_siblings_and_persists()
    {
        var path = Path.Combine(_root, "order.traceproj"); await _service.CreateAsync(path, "Order");
        Container a, b, c;
        await using (var s = await _service.OpenAsync(path))
        {
            a = await AddAsync(s, ContainerKind.Module, "A"); b = await AddAsync(s, ContainerKind.Module, "B"); c = await AddAsync(s, ContainerKind.Module, "C");
            await s.MoveContainerAsync(c.Id, null, 0);
            Assert.Equal(new[] {"C", "A", "B"}, await ChildNamesAsync(s, null));
            await s.MoveContainerAsync(a.Id, null, 2); // last
            Assert.Equal(new[] {"C", "B", "A"}, await ChildNamesAsync(s, null));
            await s.MoveContainerAsync(b.Id, null, int.MaxValue);
            Assert.Equal(new[] {"C", "A", "B"}, await ChildNamesAsync(s, null));
        }
        await using var again = await _service.OpenAsync(path);
        Assert.Equal(new[] {"C", "A", "B"}, await ChildNamesAsync(again, null));
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Move_into_a_folder_keeps_child_modules_with_it_and_rejects_cycles_and_folders_in_modules()
    {
        await using var s = await OpenAsync("reparent");
        var folder = await AddAsync(s, ContainerKind.Folder, "Docs");
        var sub = await AddAsync(s, ContainerKind.Folder, "Sub", folder.Id);
        var parent = await AddAsync(s, ContainerKind.Module, "Needs");
        var child = await AddAsync(s, ContainerKind.Module, "Reqs", parent.Id);

        await s.MoveContainerAsync(parent.Id, folder.Id, 0);
        var all = await s.ListContainersAsync();
        Assert.Equal(folder.Id, all.Single(x => x.Id == parent.Id).ParentId);
        Assert.Equal(parent.Id, all.Single(x => x.Id == child.Id).ParentId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveContainerAsync(folder.Id, sub.Id, 0));   // under its own child
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveContainerAsync(parent.Id, child.Id, 0)); // under its own child module
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveContainerAsync(sub.Id, parent.Id, 0));   // folder inside a module
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddContainerAsync(Container.Create(ContainerKind.Folder, "Bad", parent.Id)));
    }

    [Fact] public async Task Moving_a_module_away_from_the_parent_that_links_to_it_is_refused()
    {
        await using var s = await OpenAsync("links");
        var top = await AddAsync(s, ContainerKind.Module, "Needs");
        var other = await AddAsync(s, ContainerKind.Module, "Other");
        var child = await AddAsync(s, ContainerKind.Module, "Reqs", top.Id);
        var n1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "NEEDS-001", "need", top.Id);
        var r1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "REQS-001", "req", child.Id);
        await s.AddBatchAsync([n1, r1], []);
        await s.AddLinkAsync(n1.Id, r1.Id, RelationshipType.Decomposes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveContainerAsync(child.Id, other.Id, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveContainerAsync(child.Id, null, 0));
        await s.MoveContainerAsync(child.Id, top.Id, 0); // same parent: allowed (just a reorder)

        await s.RemoveLinkAsync((await s.ListRelationshipsAsync()).Single().Id);
        await s.MoveContainerAsync(child.Id, other.Id, 0); // no links any more: allowed
        Assert.Equal(other.Id, (await s.ListContainersAsync()).Single(c => c.Id == child.Id).ParentId);
    }

    [Fact] public async Task Only_an_empty_container_can_be_deleted()
    {
        var path = Path.Combine(_root, "delete.traceproj"); await _service.CreateAsync(path, "Delete");
        await using (var s = await _service.OpenAsync(path))
        {
            var folder = await AddAsync(s, ContainerKind.Folder, "Docs");
            var withObject = await AddAsync(s, ContainerKind.Module, "Full", folder.Id);
            var empty = await AddAsync(s, ContainerKind.Module, "Empty", folder.Id);
            var attribute = AttributeDefinition.Create("Priority", AttributeDataType.Text);
            await s.AddAttributeDefinitionAsync(attribute);
            await s.AssignAttributeToModuleAsync(empty.Id, attribute.Id);
            await s.SaveViewAsync(new SavedView(ObjectId.New(), empty.Id, "v", ViewDefinition.Default, DateTimeOffset.UtcNow));
            var o = EngineeringObject.Create(EngineeringObjectType.Requirement, "FULL-001", "x", withObject.Id);
            await s.AddObjectAsync(o);

            await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteContainerAsync(folder.Id));       // has modules
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteContainerAsync(withObject.Id));   // has an object
            await s.AddToOutlineAsync(withObject.Id, o.Id, null, false);
            await s.DeleteSectionAsync(withObject.Id, o.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteContainerAsync(withObject.Id));   // deleted objects still count

            await s.DeleteContainerAsync(empty.Id);                                                              // empty: saved view and column go with it
            Assert.DoesNotContain(await s.ListContainersAsync(), c => c.Id == empty.Id);
            Assert.Contains(await s.ListAuditAsync(100), a => a.Action == "container.deleted");
        }
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task A_module_with_a_baseline_cannot_be_deleted()
    {
        await using var s = await OpenAsync("baseline");
        var m = await AddAsync(s, ContainerKind.Module, "SRS");
        await s.CreateBaselineAsync(m.Id, "R1", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.DeleteContainerAsync(m.Id));
    }

    [Fact] public async Task An_attribute_column_can_be_removed_only_when_no_values_remain()
    {
        await using var s = await OpenAsync("column");
        var m = await AddAsync(s, ContainerKind.Module, "SRS");
        var attribute = AttributeDefinition.Create("Priority", AttributeDataType.Text);
        await s.AddAttributeDefinitionAsync(attribute);
        await s.AssignAttributeToModuleAsync(m.Id, attribute.Id);
        var o = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-001", "x", m.Id);
        await s.AddObjectAsync(o);
        await s.AddToOutlineAsync(m.Id, o.Id, null, false);
        await s.SetAttributeValueAsync(o.Id, attribute.Id, "high");

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RemoveAttributeFromModuleAsync(m.Id, attribute.Id));
        await s.SetAttributeValueAsync(o.Id, attribute.Id, "");
        await s.RemoveAttributeFromModuleAsync(m.Id, attribute.Id);
        Assert.Empty(await s.ListModuleAttributesAsync(m.Id));
        Assert.Single(await s.ListAttributeDefinitionsAsync()); // the definition itself stays
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RemoveAttributeFromModuleAsync(m.Id, attribute.Id)); // no longer assigned
    }
    [Fact] public async Task Names_must_be_distinct_at_one_level_but_may_repeat_elsewhere()
    {
        await using var s = await OpenAsync("names");
        var a = await AddAsync(s, ContainerKind.Module, "SRS");
        var b = await AddAsync(s, ContainerKind.Module, "SDD");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddAsync(s, ContainerKind.Module, "srs"));         // same level, case-insensitive
        await AddAsync(s, ContainerKind.Module, "SRS", b.Id);                                                        // another level: fine
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RenameContainerAsync(b.Id, "Srs"));
        await s.RenameContainerAsync(a.Id, "sRs");                                                                   // only the letter case of itself
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.MoveContainerAsync(a.Id, b.Id, 0));              // b already has an SRS
    }

    [Fact] public async Task A_deleted_object_cannot_be_linked()
    {
        await using var s = await OpenAsync("deadlink");
        var top = await AddAsync(s, ContainerKind.Module, "Needs");
        var child = await AddAsync(s, ContainerKind.Module, "Reqs", top.Id);
        var n1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "NEEDS-001", "need", top.Id);
        var r1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "REQS-001", "req", child.Id);
        await s.AddBatchAsync([n1, r1], []);
        await s.AddToOutlineAsync(child.Id, r1.Id, null, false);
        await s.DeleteSectionAsync(child.Id, r1.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddLinkAsync(n1.Id, r1.Id, RelationshipType.Decomposes));
    }

    [Fact] public async Task A_baseline_set_follows_the_tree_order_of_the_child_modules()
    {
        await using var s = await OpenAsync("setorder");
        var top = await AddAsync(s, ContainerKind.Module, "Top");
        var b = await AddAsync(s, ContainerKind.Module, "B", top.Id);
        var a = await AddAsync(s, ContainerKind.Module, "A", top.Id); // created second, so it is listed second even though "A" sorts first alphabetically
        var set = await s.CreateBaselineSetAsync(top.Id, "R1", null);
        Assert.Equal(new[] { top.Id, b.Id, a.Id }, set.Select(x => x.ModuleId).ToArray());
    }

    [Fact] public async Task A_backup_of_an_open_project_contains_the_latest_commits_and_restores()
    {
        var path = Path.Combine(_root, "live.traceproj"); await _service.CreateAsync(path, "Live");
        await using var s = await _service.OpenAsync(path); // stays open: its newest commits may still be in the write-ahead log
        await AddAsync(s, ContainerKind.Module, "Fresh");
        var archive = Path.Combine(_root, "live.zip"); var restored = Path.Combine(_root, "restored.traceproj");
        await _service.CreateBackupAsync(path, archive);
        await _service.RestoreBackupAsync(archive, restored);
        await using var again = await _service.OpenAsync(restored);
        Assert.Contains(await again.ListContainersAsync(), c => c.Name == "Fresh");
    }

    private static async Task<(Container Top, Container Child, EngineeringObject N1, EngineeringObject R1)> LinkedPairAsync(IProjectStore s)
    {
        var top = await AddAsync(s, ContainerKind.Module, "Needs");
        var child = await AddAsync(s, ContainerKind.Module, "Reqs", top.Id);
        var n1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "NEEDS-001", "need", top.Id);
        var r1 = EngineeringObject.Create(EngineeringObjectType.Requirement, "REQS-001", "req", child.Id);
        await s.AddBatchAsync([n1, r1], []);
        await s.AddToOutlineAsync(top.Id, n1.Id, null, false);
        await s.AddToOutlineAsync(child.Id, r1.Id, null, false);
        await s.AddLinkAsync(n1.Id, r1.Id, RelationshipType.Decomposes);
        return (top, child, n1, r1);
    }

    [Fact] public async Task Soft_delete_marks_a_module_tree_but_it_stays_editable_and_restore_brings_it_back()
    {
        await using var s = await OpenAsync("soft");
        var (top, child, n1, r1) = await LinkedPairAsync(s);
        await s.SoftDeleteContainerAsync(top.Id); // has content and a child module: allowed
        Assert.Empty(await s.ListContainersAsync());
        Assert.Equal(2, (await s.ListAllContainersAsync()).Count(c => c.IsDeleted));
        // the red module keeps working: objects and links stay, so links can be repaired before it is purged
        Assert.Equal(2, (await s.ListObjectsAsync()).Count);
        Assert.Single(await s.ListRelationshipsAsync());
        await s.RenameObjectAsync(r1.Id, "edited while red");
        var extra = EngineeringObject.Create(EngineeringObjectType.Requirement, "REQS-002", "new", child.Id);
        await s.AddObjectAsync(extra);
        await s.RestoreContainerAsync(top.Id);
        Assert.Equal(2, (await s.ListContainersAsync()).Count);
        Assert.Equal(3, (await s.ListObjectsAsync()).Count);
    }

    [Fact] public async Task Restore_needs_a_live_parent_and_a_free_name_and_purge_needs_a_deleted_item()
    {
        await using var s = await OpenAsync("restore-rules");
        var top = await AddAsync(s, ContainerKind.Module, "Top");
        var child = await AddAsync(s, ContainerKind.Module, "Kid", top.Id);
        await s.SoftDeleteContainerAsync(child.Id);
        await s.SoftDeleteContainerAsync(top.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RestoreContainerAsync(child.Id)); // parent still deleted
        var live = await AddAsync(s, ContainerKind.Module, "Top"); // name is free again while the old one is deleted
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.PurgeContainerAsync(live.Id)); // a live item cannot be purged
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RestoreContainerAsync(top.Id)); // clash
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SoftDeleteContainerAsync(top.Id)); // already deleted
    }


    [Fact] public async Task Purge_destroys_a_deleted_module_tree_with_its_objects_and_history()
    {
        await using var s = await OpenAsync("purge");
        var keep = await AddAsync(s, ContainerKind.Module, "Keep");
        var (top, child, n1, r1) = await LinkedPairAsync(s);
        await s.RenameObjectAsync(r1.Id, "second revision"); // creates revision history rows
        await s.SoftDeleteContainerAsync(top.Id);
        await s.PurgeContainerAsync(top.Id);
        Assert.Equal(keep.Id, Assert.Single(await s.ListAllContainersAsync()).Id);
        Assert.Empty(await s.ListModuleObjectsAsync(child.Id));
        Assert.Contains(await s.ListAuditAsync(50), a => a.Action == "container.purged");
        // the append-only guarantees are back in place afterwards
        var o = EngineeringObject.Create(EngineeringObjectType.Requirement, "KEEP-001", "x", keep.Id);
        await s.AddObjectAsync(o);
        await s.RenameObjectAsync(o.Id, "y");
        Assert.True((await s.CheckIntegrityAsync()).IsValid);
    }

    [Fact] public async Task Purge_is_refused_when_the_module_has_a_baseline()
    {
        await using var s = await OpenAsync("purge-baseline");
        var m = await AddAsync(s, ContainerKind.Module, "SRS");
        var o = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-001", "x", m.Id);
        await s.AddObjectAsync(o);
        await s.AddToOutlineAsync(m.Id, o.Id, null, false);
        await s.CreateBaselineAsync(m.Id, "R1", null);
        await s.SoftDeleteContainerAsync(m.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.PurgeContainerAsync(m.Id));
        Assert.Single(await s.ListAllContainersAsync()); // rolled back, still there
    }

    [Fact] public async Task A_deleted_module_cannot_receive_new_child_modules()
    {
        await using var s = await OpenAsync("deleted-target");
        var m = await AddAsync(s, ContainerKind.Module, "SRS");
        await s.SoftDeleteContainerAsync(m.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddAsync(s, ContainerKind.Module, "Kid", m.Id));
    }

    // ----- what the app's Undo for tree actions relies on -----

    [Fact] public async Task Restore_then_move_back_puts_a_deleted_module_in_its_old_place()
    {
        await using var s = await OpenAsync("undo-delete");
        await AddAsync(s, ContainerKind.Module, "A"); var b = await AddAsync(s, ContainerKind.Module, "B"); await AddAsync(s, ContainerKind.Module, "C");
        await s.SoftDeleteContainerAsync(b.Id);
        await s.RestoreContainerAsync(b.Id); // restore alone puts it last
        Assert.Equal(new[] {"A", "C", "B"}, await ChildNamesAsync(s, null));
        await s.MoveContainerAsync(b.Id, null, 1);
        Assert.Equal(new[] {"A", "B", "C"}, await ChildNamesAsync(s, null));
    }

    [Fact] public async Task Moving_back_to_the_old_parent_and_index_reverses_a_move()
    {
        await using var s = await OpenAsync("undo-move");
        var a = await AddAsync(s, ContainerKind.Folder, "A"); await AddAsync(s, ContainerKind.Folder, "B"); var c = await AddAsync(s, ContainerKind.Folder, "C");
        await s.MoveContainerAsync(c.Id, a.Id, int.MaxValue);
        Assert.Equal(new[] {"A", "B"}, await ChildNamesAsync(s, null));
        await s.MoveContainerAsync(c.Id, null, 2);
        Assert.Equal(new[] {"A", "B", "C"}, await ChildNamesAsync(s, null));
        Assert.Empty(await ChildNamesAsync(s, a.Id));
    }

    // ----- integrity warning for old duplicate names -----

    [Fact] public async Task Check_warns_about_legacy_duplicate_sibling_names_but_the_project_stays_valid()
    {
        var path = Path.Combine(_root, "dupes.traceproj");
        await _service.CreateAsync(path, "dupes");
        await using (var s = await _service.OpenAsync(path))
        {
            await AddAsync(s, ContainerKind.Module, "SRS");
            await AddAsync(s, ContainerKind.Module, "SRS2");
        }
        await RawAsync(Path.Combine(path, "project.db"), "UPDATE containers SET name='srs' WHERE name='SRS2'"); // an old project could hold this
        var report = await _service.CheckAsync(path);
        Assert.True(report.IsValid);
        Assert.Contains(report.Warnings!, w => w.StartsWith("[naming]"));
    }

    [Fact] public async Task Check_does_not_warn_when_the_only_same_name_is_a_deleted_one()
    {
        var path = Path.Combine(_root, "nodupes.traceproj");
        await _service.CreateAsync(path, "nodupes");
        await using (var s = await _service.OpenAsync(path))
        {
            var old = await AddAsync(s, ContainerKind.Module, "SRS");
            await s.SoftDeleteContainerAsync(old.Id);
            await AddAsync(s, ContainerKind.Module, "SRS");
        }
        Assert.DoesNotContain((await _service.CheckAsync(path)).Warnings!, w => w.StartsWith("[naming]"));
    }

    // ----- unused attachment files -----

    private async Task<(string Path, string FileId)> ProjectWithAttachmentAsync(string name, Func<IProjectStore, Container, Task> beforeClose)
    {
        var path = Path.Combine(_root, name + ".traceproj");
        await _service.CreateAsync(path, name);
        var source = Path.Combine(_root, name + ".txt");
        await File.WriteAllTextAsync(source, "evidence " + name);
        string fileId;
        await using (var s = await _service.OpenAsync(path))
        {
            var m = await AddAsync(s, ContainerKind.Module, "SRS");
            var o = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-001", "x", m.Id);
            await s.AddObjectAsync(o);
            await s.AddToOutlineAsync(m.Id, o.Id, null, false);
            var managed = await _service.ImportManagedAttachmentAsync(path, source);
            await s.AddAttachmentAsync(o.Id, managed.Id, "evidence.txt", managed.SizeBytes, managed.Sha256);
            fileId = managed.Id;
            await beforeClose(s, m);
        }
        return (path, fileId);
    }

    [Fact] public async Task Files_left_after_a_permanent_delete_are_found_and_removed_and_the_project_stays_valid()
    {
        var (path, fileId) = await ProjectWithAttachmentAsync("cleanup", async (s, m) => { await s.SoftDeleteContainerAsync(m.Id); await s.PurgeContainerAsync(m.Id); });
        await File.WriteAllTextAsync(Path.Combine(path, "attachments", "interrupted.bin.staging"), "partial");
        IReadOnlySet<string> referenced;
        await using (var s = await _service.OpenAsync(path)) referenced = await s.ListReferencedAttachmentIdsAsync();
        Assert.DoesNotContain(fileId, referenced);
        Assert.Equal(2, (await _service.FindUnusedAttachmentsAsync(path, referenced)).Count);
        Assert.Equal(2, await _service.DeleteUnusedAttachmentsAsync(path, referenced));
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "attachments")));
        var report = await _service.CheckAsync(path);
        Assert.True(report.IsValid, string.Join("; ", report.Findings));
        Assert.Empty(report.Warnings!);
    }

    [Fact] public async Task Files_of_used_and_of_merely_removed_attachments_are_never_listed_as_unused()
    {
        var (path, fileId) = await ProjectWithAttachmentAsync("kept", async (s, m) =>
        {
            var o = (await s.ListModuleObjectsAsync(m.Id)).Single();
            await s.RemoveAttachmentAsync((await s.ListAttachmentsAsync(o.Id)).Single().Id); // hidden, but the record and file stay for the history
        });
        IReadOnlySet<string> referenced;
        await using (var s = await _service.OpenAsync(path)) referenced = await s.ListReferencedAttachmentIdsAsync();
        Assert.Contains(fileId, referenced);
        Assert.Empty(await _service.FindUnusedAttachmentsAsync(path, referenced));
        Assert.Equal(0, await _service.DeleteUnusedAttachmentsAsync(path, referenced));
        Assert.Single(Directory.GetFiles(Path.Combine(path, "attachments")));
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }
}
