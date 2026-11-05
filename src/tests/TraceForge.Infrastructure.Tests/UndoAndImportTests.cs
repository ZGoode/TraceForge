using TraceForge.Application;
using TraceForge.Domain;
using TraceForge.Infrastructure;
using Xunit;

namespace TraceForge.Infrastructure.Tests;

public sealed class UndoStackTests
{
    private static UndoEntry Entry(string name, List<string> log) => new(name, null, null, () => { log.Add("undo " + name); return Task.CompletedTask; }, () => { log.Add("redo " + name); return Task.CompletedTask; });

    [Fact] public async Task Undo_and_redo_run_last_in_first_out_and_a_new_action_clears_redo()
    {
        var log = new List<string>(); var stack = new UndoStack();
        stack.Push(Entry("a", log)); stack.Push(Entry("b", log));
        Assert.Equal("b", stack.UndoText);
        await stack.UndoAsync(); await stack.UndoAsync();
        Assert.False(stack.CanUndo); Assert.Equal("a", stack.RedoText);
        await stack.RedoAsync();
        Assert.Equal(new[] { "undo b", "undo a", "redo a" }, log);
        stack.Push(Entry("c", log));
        Assert.False(stack.CanRedo);
    }

    [Fact] public async Task Limit_drops_the_oldest_and_a_failing_undo_discards_its_entry()
    {
        var stack = new UndoStack(2); var log = new List<string>();
        stack.Push(Entry("a", log)); stack.Push(Entry("b", log)); stack.Push(Entry("c", log));
        await stack.UndoAsync(); await stack.UndoAsync();
        Assert.False(stack.CanUndo);
        stack.Push(new UndoEntry("bad", null, null, () => throw new InvalidOperationException("changed underneath"), () => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stack.UndoAsync());
        Assert.False(stack.CanUndo); Assert.False(stack.CanRedo);
        Assert.Null(await stack.UndoAsync());
    }
}

public sealed class RestoreAndImportStoreTests : TempDirectoryTestBase
{
    private readonly ProjectPackageService _service = new();

    private sealed record Seed(Container Module, Container Child, EngineeringObject A, EngineeringObject B, EngineeringObject ChildReq, AttributeDefinition Priority, AttributeDefinition Owner);

    private static async Task<Seed> SeedAsync(IProjectStore s)
    {
        var module = Container.Create(ContainerKind.Module, "SRS");
        var child = Container.Create(ContainerKind.Module, "SDD", module.Id);
        await s.AddContainerAsync(module); await s.AddContainerAsync(child);
        var a = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-001", "Boot", module.Id);
        var b = EngineeringObject.Create(EngineeringObjectType.Requirement, "SRS-002", "Log", module.Id);
        var childReq = EngineeringObject.Create(EngineeringObjectType.Requirement, "SDD-001", "Loader", child.Id);
        await s.AddBatchAsync([a, b, childReq], []);
        await s.AddToOutlineAsync(module.Id, a.Id, null, false); await s.AddToOutlineAsync(module.Id, b.Id, null, false); await s.AddToOutlineAsync(child.Id, childReq.Id, null, false);
        var priority = AttributeDefinition.Create("Priority", AttributeDataType.Enumeration, false, new[] { "low", "high" });
        var owner = AttributeDefinition.Create("Owner", AttributeDataType.Text, true, Array.Empty<string>());
        foreach (var d in new[] { priority, owner }) { await s.AddAttributeDefinitionAsync(d); await s.AssignAttributeToModuleAsync(module.Id, d.Id); }
        await s.SetAttributeValueAsync(a.Id, owner.Id, "team");
        return new(module, child, a, b, childReq, priority, owner);
    }

    [Fact] public async Task Restoring_a_revision_creates_a_new_revision_and_never_rewrites_history()
    {
        var path = Path.Combine(_root, "r.traceproj"); await _service.CreateAsync(path, "Restore");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        await s.SetAttributeValueAsync(f.A.Id, f.Priority.Id, "low");               // rev 3 (rev 2 = owner)
        var before = (await s.ListRevisionsAsync(f.A.Id)).Single(r => r.Revision == 3);
        await s.RenameObjectAsync(f.A.Id, "Boot under 2 s");                         // rev 4
        await s.SetAttributeValueAsync(f.A.Id, f.Priority.Id, "high");               // rev 5
        await s.SetAttributeValueAsync(f.A.Id, f.Owner.Id, "other-team");            // rev 6

        Assert.True(await s.RestoreRevisionAsync(f.A.Id, 3));
        var revisions = await s.ListRevisionsAsync(f.A.Id);
        Assert.Equal(7, revisions[0].Revision); Assert.StartsWith("Restored revision 3", revisions[0].Summary);
        Assert.Equal(before.Title, revisions[0].Title);
        Assert.Equal(before.Values, revisions[0].Values);                             // text, priority and owner all as in revision 3
        Assert.Equal(7, revisions.Count);                                             // nothing removed
        Assert.False(await s.RestoreRevisionAsync(f.A.Id, 3));                       // already identical: no new revision
        Assert.Equal(7, (await s.ListRevisionsAsync(f.A.Id)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RestoreRevisionAsync(f.A.Id, 99));
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Restoring_keeps_required_values_and_ignores_attributes_that_are_no_longer_assigned()
    {
        var path = Path.Combine(_root, "q.traceproj"); await _service.CreateAsync(path, "Required");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);                                                   // rev 1 = created (no owner), rev 2 = owner "team"
        await s.SetAttributeValueAsync(f.B.Id, f.Owner.Id, "team-b");                // B: rev 2
        Assert.False(await s.RestoreRevisionAsync(f.B.Id, 1));                       // revision 1 had no Owner, but it is required: kept, so nothing changes
        Assert.Equal("team-b", (await s.ListAttributeValuesAsync(f.Module.Id)).Single(v => v.ObjectId == f.B.Id && v.AttributeId == f.Owner.Id).Value);
    }

    [Fact] public async Task Restoring_a_deleted_section_can_return_it_to_its_original_position()
    {
        var path = Path.Combine(_root, "p.traceproj"); await _service.CreateAsync(path, "Position");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);                                                   // outline: A, B
        var deleted = await s.DeleteSectionAsync(f.Module.Id, f.A.Id);
        Assert.Equal(f.A.Id, deleted.RootId);
        await s.RestoreDeletedAsync(deleted.BatchId, 0);
        var numbers = Outline.Number(await s.GetOutlineAsync(f.Module.Id));
        Assert.Equal("1", numbers[f.A.Id]); Assert.Equal("2", numbers[f.B.Id]);
    }

    [Fact] public async Task Import_applies_attributes_creates_updates_and_links_in_one_transaction_and_reports_what_to_undo()
    {
        var path = Path.Combine(_root, "i.traceproj"); await _service.CreateAsync(path, "Import");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var notes = AttributeDefinition.Create("Notes", AttributeDataType.Text, false, Array.Empty<string>());
        EngineeringObject N(string id, string title) => EngineeringObject.Create(EngineeringObjectType.Requirement, id, title, f.Module.Id);
        var heading = N("SRS-010", "New heading"); var kid = N("SRS-011", "New child");
        var creates = new[]
        {
            new SectionItem(heading, null, true, new Dictionary<string, string>()),
            new SectionItem(kid, heading.Id, false, new Dictionary<string, string> { [notes.Id.ToString()] = "imported", [f.Owner.Id.ToString()] = "team-x" }),
        };
        var updates = new[] { new ObjectUpdate(f.A.Id, "Boot under 2 s", new Dictionary<string, string> { [f.Priority.Id.ToString()] = "high" }) };
        var links = new[] { (kid.Id, f.ChildReq.Id, RelationshipType.Satisfies) };

        var outcome = await s.ImportAsync(f.Module.Id, [notes], [], creates, updates, links);
        Assert.Equal(new[] { heading.Id, kid.Id }, outcome.Created);
        Assert.Equal(f.A.Id, outcome.Updated.Single().Id); Assert.Equal(2, outcome.Updated.Single().PreviousRevision);
        Assert.Single(outcome.LinkIds); Assert.Equal(1, outcome.NewAttributes);
        Assert.Contains(await s.ListModuleAttributesAsync(f.Module.Id), a => a.Name == "Notes");
        Assert.Equal("Boot under 2 s", (await s.ListObjectsAsync()).Single(o => o.Id == f.A.Id).Title);
        var numbers = Outline.Number(await s.GetOutlineAsync(f.Module.Id));
        Assert.Equal("3", numbers[heading.Id]); Assert.Equal("3.1", numbers[kid.Id]);
        Assert.Single(await s.ListRelationshipsAsync());
        Assert.True((await _service.CheckAsync(path)).IsValid);

        // all or nothing: a bad link (target not in a child module) rolls back creates, updates and the new attribute
        var more = N("SRS-020", "Should not stay");
        var other = AttributeDefinition.Create("Another", AttributeDataType.Text, false, Array.Empty<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.ImportAsync(f.Module.Id, [other], [], [new SectionItem(more, null, false, new Dictionary<string, string>())],
            [new ObjectUpdate(f.B.Id, "Log changed", new Dictionary<string, string>())], [(more.Id, f.A.Id, RelationshipType.Decomposes)]));
        Assert.DoesNotContain(await s.ListObjectsAsync(), o => o.CanonicalId == "SRS-020");
        Assert.Equal("Log", (await s.ListObjectsAsync()).Single(o => o.Id == f.B.Id).Title);
        Assert.DoesNotContain(await s.ListAttributeDefinitionsAsync(), a => a.Name == "Another");
    }

    [Fact] public async Task A_link_becomes_suspect_when_either_end_changes_and_clears_when_reviewed()
    {
        var path = Path.Combine(_root, "suspect.traceproj"); await _service.CreateAsync(path, "Suspect");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var link = await s.AddLinkAsync(f.A.Id, f.ChildReq.Id, RelationshipType.Decomposes);
        Assert.Empty(await s.ListSuspectLinksAsync(null));

        await s.RenameObjectAsync(f.A.Id, "Boot under 3 s");
        var suspect = Assert.Single(await s.ListSuspectLinksAsync(f.Module.Id));
        Assert.Equal(link.Id, suspect.RelationshipId); Assert.True(suspect.SourceChanged); Assert.False(suspect.TargetChanged);
        Assert.Single(await s.ListSuspectLinksAsync(f.Child.Id));                       // visible from the child module too
        Assert.Empty(await s.ListSuspectLinksAsync(ObjectId.New()));

        await s.ClearSuspectAsync([link.Id]);
        Assert.Empty(await s.ListSuspectLinksAsync(null));

        await s.RenameObjectAsync(f.ChildReq.Id, "Loader v2");
        suspect = Assert.Single(await s.ListSuspectLinksAsync(null));
        Assert.False(suspect.SourceChanged); Assert.True(suspect.TargetChanged);

        await s.RemoveLinkAsync(link.Id);
        Assert.Empty(await s.ListSuspectLinksAsync(null));
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Comments_form_one_level_threads_resolve_by_event_and_can_never_be_edited_or_deleted()
    {
        var path = Path.Combine(_root, "comments.traceproj"); await _service.CreateAsync(path, "Comments");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var root = await s.AddCommentAsync(f.A.Id, null, "  Is 2 s realistic?  ");
        var reply = await s.AddCommentAsync(f.A.Id, root.Id, "Yes, measured.");
        Assert.Equal("Is 2 s realistic?", root.Body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddCommentAsync(f.A.Id, reply.Id, "nested"));      // replies cannot be replied to
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddCommentAsync(f.B.Id, root.Id, "wrong object")); // thread belongs to another object
        await Assert.ThrowsAsync<ArgumentException>(() => s.AddCommentAsync(f.A.Id, null, "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => s.AddCommentAsync(f.A.Id, null, new string('x', Comment.MaxLength + 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddCommentAsync(ObjectId.New(), null, "ghost"));

        var comments = await s.ListCommentsAsync(f.A.Id);
        Assert.Equal(2, comments.Count); Assert.All(comments, c => Assert.False(c.Resolved));
        Assert.Equal(1, (await s.CountOpenThreadsAsync(f.Module.Id))[f.A.Id]);

        await s.SetThreadResolvedAsync(root.Id, true);
        Assert.True((await s.ListCommentsAsync(f.A.Id)).Single(c => c.Id == root.Id).Resolved);
        Assert.Empty(await s.CountOpenThreadsAsync(f.Module.Id));
        await s.SetThreadResolvedAsync(root.Id, false);
        Assert.Equal(1, (await s.CountOpenThreadsAsync(f.Module.Id))[f.A.Id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SetThreadResolvedAsync(reply.Id, true));          // only thread roots resolve

        Assert.Single(await s.ListRevisionsAsync(f.B.Id));                                                       // comments do not create revisions
        Assert.Empty(await s.ListSuspectLinksAsync(null));
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task Attachments_are_managed_files_recorded_per_object_and_removal_keeps_the_file()
    {
        var path = Path.Combine(_root, "attach.traceproj"); await _service.CreateAsync(path, "Attach");
        var source = Path.Combine(_root, "spec.pdf"); await File.WriteAllTextAsync(source, "not really a pdf");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var managed = await _service.ImportManagedAttachmentAsync(path, source);
        var record = await s.AddAttachmentAsync(f.A.Id, managed.Id, @"C:\temp\..\spec.pdf", managed.SizeBytes, managed.Sha256);
        Assert.Equal("spec.pdf", record.FileName);                                                                    // only the file name is kept
        Assert.Equal(1, (await s.CountAttachmentsAsync(f.Module.Id))[f.A.Id]);
        Assert.Single(await s.ListAttachmentsAsync(f.A.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddAttachmentAsync(f.A.Id, managed.Id, "big.bin", ObjectAttachment.MaxBytes + 1, managed.Sha256));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddAttachmentAsync(ObjectId.New(), managed.Id, "x.txt", 1, "00"));

        await s.RemoveAttachmentAsync(record.Id);
        Assert.Empty(await s.ListAttachmentsAsync(f.A.Id));
        Assert.True(File.Exists(Path.Combine(path, managed.RelativePath)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RemoveAttachmentAsync(record.Id));
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }

    [Fact] public async Task The_audit_log_lists_newest_first_honours_the_limit_and_cannot_be_changed()
    {
        var path = Path.Combine(_root, "audit.traceproj"); await _service.CreateAsync(path, "Audit");
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        await s.RenameObjectAsync(f.A.Id, "Renamed");
        var all = await s.ListAuditAsync(5000);
        Assert.True(all.Count >= 3);
        Assert.Equal("object.renamed", all[0].Action);
        Assert.True(all.Zip(all.Skip(1), (newer, older) => newer.Seq > older.Seq).All(x => x));
        Assert.Equal(2, (await s.ListAuditAsync(2)).Count);
        Assert.Single(await s.ListAuditAsync(-5));                                      // limit is clamped to at least 1
    }

    [Fact] public async Task A_file_can_be_attached_to_a_comment_of_the_same_object_only()
    {
        var path = Path.Combine(_root, "commentfile.traceproj"); await _service.CreateAsync(path, "CommentFile");
        var source = Path.Combine(_root, "photo.png"); await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        await using var s = await _service.OpenAsync(path);
        var f = await SeedAsync(s);
        var comment = await s.AddCommentAsync(f.A.Id, null, "See photo");
        var managed = await _service.ImportManagedAttachmentAsync(path, source);

        var onComment = await s.AddAttachmentAsync(f.A.Id, managed.Id, "photo.png", managed.SizeBytes, managed.Sha256, comment.Id);
        Assert.Equal(comment.Id, onComment.CommentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddAttachmentAsync(f.B.Id, managed.Id, "photo.png", managed.SizeBytes, managed.Sha256, comment.Id)); // comment belongs to A
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddAttachmentAsync(f.A.Id, managed.Id, "photo.png", managed.SizeBytes, managed.Sha256, ObjectId.New()));

        await s.AddAttachmentAsync(f.A.Id, managed.Id, "spec.pdf", managed.SizeBytes, managed.Sha256);
        var all = await s.ListAttachmentsAsync(f.A.Id);
        Assert.Equal(2, all.Count);
        Assert.Single(all, a => a.CommentId == comment.Id);
        Assert.Single(all, a => a.CommentId is null);
        Assert.True((await _service.CheckAsync(path)).IsValid);
    }
}

