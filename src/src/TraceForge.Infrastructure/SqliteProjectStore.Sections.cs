using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Delete, restore, paste and import of outline sections (schema v6).</summary>
public sealed partial class SqliteProjectStore
{
    // ---------- delete, restore, paste (schema v6) ----------

    public async Task<IReadOnlyList<string>> ListAllCanonicalIdsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<string>();
        await using var c = Cmd(null, "SELECT canonical_id FROM engineering_objects");
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<DeletedSection> DeleteSectionAsync(ObjectId moduleId, ObjectId rootObjectId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        var outline = await ReadOutlineAsync(moduleId, tx, cancellationToken);
        if (outline.All(e => e.ObjectId != rootObjectId)) throw new InvalidOperationException("The object is not in this module's outline.");
        var children = outline.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        var ids = new List<ObjectId> { rootObjectId };
        for (var i = 0; i < ids.Count; i++) ids.AddRange(children[ids[i]].Select(e => e.ObjectId));
        var batch = ObjectId.New().ToString();
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var id in ids)
        {
            var canonical = await ScalarAsync(tx, "SELECT canonical_id FROM engineering_objects WHERE id=$o", cancellationToken, ("$o", id.ToString()));
            await using (var up = Cmd(tx, "UPDATE engineering_objects SET deleted_utc=$now, delete_batch=$b WHERE id=$o"))
            {
                up.Parameters.AddWithValue("$now", now); up.Parameters.AddWithValue("$b", batch); up.Parameters.AddWithValue("$o", id.ToString());
                await up.ExecuteNonQueryAsync(cancellationToken);
            }
            await AuditAsync(tx, "object.deleted", "object", id.ToString(), $"{canonical} (batch {batch})", cancellationToken);
        }
        var rootRow = await ReadDeletedAsync(tx, "o.id = $arg", rootObjectId.ToString(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new DeletedSection(ObjectId.Parse(batch), rootObjectId, rootRow[0].CanonicalId, rootRow[0].Title, ids.Count, DateTimeOffset.Parse(now, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private sealed record DeletedRow(ObjectId Id, string BatchId, string CanonicalId, string Title, ObjectId? ParentId, ObjectId ModuleId, DateTimeOffset DeletedUtc);

    private async Task<List<DeletedRow>> ReadDeletedAsync(SqliteTransaction? tx, string where, string? arg, CancellationToken ct)
    {
        var rows = new List<DeletedRow>();
        await using var c = Cmd(tx, "SELECT o.id, o.delete_batch, o.canonical_id, o.title, e.parent_object_id, e.module_id, o.deleted_utc FROM engineering_objects o JOIN outline_entries e ON e.object_id = o.id WHERE o.delete_batch IS NOT NULL AND " + where + " ORDER BY o.deleted_utc DESC, e.sort_order, o.canonical_id");
        if (arg is not null) c.Parameters.AddWithValue("$arg", arg);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new DeletedRow(ObjectId.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? (ObjectId?)null : ObjectId.Parse(r.GetString(4)), ObjectId.Parse(r.GetString(5)), ParseTime(r.GetString(6))));
        return rows;
    }

    private static DeletedRow RootOf(IReadOnlyCollection<DeletedRow> batch)
    {
        var ids = batch.Select(b => b.Id).ToHashSet();
        return batch.First(b => b.ParentId is null || !ids.Contains(b.ParentId.Value));
    }

    public async Task<IReadOnlyList<DeletedSection>> ListDeletedAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var rows = await ReadDeletedAsync(null, "e.module_id = $arg", moduleId.ToString(), cancellationToken);
        return rows.GroupBy(r => r.BatchId).Select(g => { var root = RootOf(g.ToList()); return new DeletedSection(ObjectId.Parse(g.Key), root.Id, root.CanonicalId, root.Title, g.Count(), root.DeletedUtc); })
            .OrderByDescending(d => d.DeletedUtc).ToList();
    }

    public async Task<int> RestoreDeletedAsync(ObjectId batchId, int? index = null, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var batch = await ReadDeletedAsync(tx, "o.delete_batch = $arg", batchId.ToString(), cancellationToken);
        if (batch.Count == 0) throw new InvalidOperationException("Nothing to restore: that deleted section no longer exists.");
        var root = RootOf(batch);
        if (root.ParentId is { } parent && Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM engineering_objects WHERE id=$p AND deleted_utc IS NOT NULL", cancellationToken, ("$p", parent.ToString())), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException("The section's parent is deleted too. Restore the parent section first.");
        await using (var up = Cmd(tx, "UPDATE engineering_objects SET deleted_utc=NULL, delete_batch=NULL WHERE delete_batch=$b"))
        {
            up.Parameters.AddWithValue("$b", batchId.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        var next = Convert.ToInt32(await ScalarAsync(tx, "SELECT COALESCE(MAX(e.sort_order)+1,0) FROM outline_entries e WHERE e.module_id=$m AND e.parent_object_id IS $p AND e.object_id <> $r", cancellationToken,
            ("$m", root.ModuleId.ToString()), ("$p", root.ParentId?.ToString()), ("$r", root.Id.ToString())), CultureInfo.InvariantCulture);
        await using (var up = Cmd(tx, "UPDATE outline_entries SET sort_order=$s WHERE object_id=$o"))
        {
            up.Parameters.AddWithValue("$s", next); up.Parameters.AddWithValue("$o", root.Id.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        if (index is { } position) await ApplyOutlineMoveAsync(tx, root.ModuleId, root.Id, root.ParentId, position, cancellationToken);
        foreach (var row in batch) await AuditAsync(tx, "object.restored", "object", row.Id.ToString(), $"{row.CanonicalId} (batch {batchId})", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return batch.Count;
    }

    public async Task AddSectionAsync(ObjectId moduleId, ObjectId? parentId, int? index, IReadOnlyList<SectionItem> items, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (items.Count == 0) throw new ArgumentException("Nothing to add.", nameof(items));
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await AddSectionCoreAsync(tx, moduleId, parentId, index, items, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task AddSectionCoreAsync(SqliteTransaction tx, ObjectId moduleId, ObjectId? parentId, int? index, IReadOnlyList<SectionItem> items, CancellationToken cancellationToken)
    {
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        var live = await ReadOutlineAsync(moduleId, tx, cancellationToken);
        if (parentId is { } p && live.All(e => e.ObjectId != p)) throw new InvalidOperationException("The parent is not in this module's outline.");
        var assigned = (await ReadDefinitionsAsync("SELECT " + DefinitionColumns + " FROM attribute_definitions d JOIN module_attributes ma ON ma.attribute_id = d.id WHERE ma.module_id=$m", ("$m", moduleId.ToString()), cancellationToken, tx)).ToDictionary(d => d.Id.ToString());
        var created = new HashSet<ObjectId>();
        foreach (var item in items)
        {
            if (item.Object.ContainerId != moduleId) throw new InvalidOperationException("Pasted objects must belong to the target module.");
            var parent = item.ParentObjectId ?? parentId;
            if (item.ParentObjectId is { } ip && !created.Contains(ip) && live.All(e => e.ObjectId != ip)) throw new InvalidOperationException("A parent must be listed before its children or already be in the module's outline.");
            await InsertObjectAsync(tx, item.Object, cancellationToken);
            await AuditAsync(tx, "object.created", "object", item.Object.Id.ToString(), $"{item.Object.Type} {item.Object.CanonicalId} (pasted)", cancellationToken);
            var next = Convert.ToInt32(await ScalarAsync(tx, "SELECT COALESCE(MAX(sort_order)+1,0) FROM outline_entries WHERE module_id=$m AND parent_object_id IS $p", cancellationToken, ("$m", moduleId.ToString()), ("$p", parent?.ToString())), CultureInfo.InvariantCulture);
            await using (var c = Cmd(tx, "INSERT INTO outline_entries(object_id,module_id,parent_object_id,sort_order,is_heading) VALUES ($o,$m,$p,$s,$h)"))
            {
                c.Parameters.AddWithValue("$o", item.Object.Id.ToString()); c.Parameters.AddWithValue("$m", moduleId.ToString());
                c.Parameters.AddWithValue("$p", DbId(parent));
                c.Parameters.AddWithValue("$s", next); c.Parameters.AddWithValue("$h", item.IsHeading ? 1 : 0);
                await c.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var (attributeId, value) in item.Values)
            {
                if (!assigned.TryGetValue(attributeId, out var definition) || !definition.TryNormalize(value, out var normalized, out _)) continue; // not on this module, or invalid there
                await using var v = Cmd(tx, "INSERT INTO attribute_values(object_id,attribute_id,value) VALUES ($o,$a,$v)");
                v.Parameters.AddWithValue("$o", item.Object.Id.ToString()); v.Parameters.AddWithValue("$a", attributeId); v.Parameters.AddWithValue("$v", normalized);
                await v.ExecuteNonQueryAsync(cancellationToken);
            }
            await RecordRevisionAsync(tx, item.Object.Id, false, "Created", cancellationToken);
            created.Add(item.Object.Id);
        }
        if (index is { } i) await ApplyOutlineMoveAsync(tx, moduleId, items[0].Object.Id, parentId, i, cancellationToken);
        await AuditAsync(tx, "section.pasted", "object", items[0].Object.Id.ToString(), $"{items.Count} object(s) into module {moduleId}", cancellationToken);
    }

    public async Task<ImportOutcome> ImportAsync(ObjectId moduleId, IReadOnlyList<AttributeDefinition> newAttributes, IReadOnlyList<ObjectId> assignAttributes,
        IReadOnlyList<SectionItem> creates, IReadOnlyList<ObjectUpdate> updates, IReadOnlyList<(ObjectId Source, ObjectId Target, RelationshipType Type)> links,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        foreach (var definition in newAttributes) { await AddAttributeDefinitionCoreAsync(tx, definition, cancellationToken); await AssignAttributeCoreAsync(tx, moduleId, definition.Id, cancellationToken); }
        foreach (var id in assignAttributes) await AssignAttributeCoreAsync(tx, moduleId, id, cancellationToken);

        var updated = new List<(ObjectId Id, long PreviousRevision)>();
        foreach (var update in updates)
        {
            var revision = Convert.ToInt64(await ScalarAsync(tx, "SELECT revision FROM engineering_objects WHERE id=$o AND container_id=$m AND deleted_utc IS NULL", cancellationToken, ("$o", update.ObjectId.ToString()), ("$m", moduleId.ToString()))
                ?? throw new InvalidOperationException("An object to update does not exist in this module."), CultureInfo.InvariantCulture);
            if (update.NewTitle is not null) await RenameObjectCoreAsync(tx, update.ObjectId, update.NewTitle, cancellationToken);
            foreach (var (attributeId, value) in update.Values) await SetAttributeValueCoreAsync(tx, update.ObjectId, ObjectId.Parse(attributeId), value, cancellationToken);
            updated.Add((update.ObjectId, revision));
        }
        if (creates.Count > 0) await AddSectionCoreAsync(tx, moduleId, null, null, creates, cancellationToken);
        var linkIds = new List<ObjectId>();
        foreach (var (source, target, type) in links) linkIds.Add((await AddLinkCoreAsync(tx, source, target, type, cancellationToken)).Id);
        await AuditAsync(tx, "import.applied", "container", moduleId.ToString(), $"{creates.Count} created, {updates.Count} updated, {linkIds.Count} link(s), {newAttributes.Count} new attribute(s)", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ImportOutcome(creates.Select(c => c.Object.Id).ToList(), updated, linkIds, newAttributes.Count);
    }
}
