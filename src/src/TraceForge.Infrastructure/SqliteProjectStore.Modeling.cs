using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Modules, outline, attributes, links, suspect links and the audit log.</summary>
public sealed partial class SqliteProjectStore
{
    // ---------- modules, outline, attributes ----------

    public async Task AddAttributeDefinitionAsync(AttributeDefinition value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await AddAttributeDefinitionCoreAsync(tx, value, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task AddAttributeDefinitionCoreAsync(SqliteTransaction tx, AttributeDefinition value, CancellationToken cancellationToken)
    {
        await using (var c = Cmd(tx, "INSERT INTO attribute_definitions(id,name,data_type,is_required,enum_values,created_utc) VALUES ($id,$name,$type,$required,$enum,$created)"))
        {
            c.Parameters.AddWithValue("$id", value.Id.ToString());
            c.Parameters.AddWithValue("$name", value.Name);
            c.Parameters.AddWithValue("$type", (int)value.DataType);
            c.Parameters.AddWithValue("$required", value.IsRequired ? 1 : 0);
            c.Parameters.AddWithValue("$enum", JsonSerializer.Serialize(value.EnumValues));
            c.Parameters.AddWithValue("$created", value.CreatedUtc.ToString("O"));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "attribute.defined", "attribute", value.Id.ToString(), $"{value.Name} ({value.DataType})", cancellationToken);
    }

    public async Task<IReadOnlyList<AttributeDefinition>> ListAttributeDefinitionsAsync(CancellationToken cancellationToken = default) =>
        await ReadDefinitionsAsync("SELECT " + DefinitionColumns + " FROM attribute_definitions d ORDER BY d.name COLLATE NOCASE", null, cancellationToken);

    public async Task AssignAttributeToModuleAsync(ObjectId moduleId, ObjectId attributeId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await AssignAttributeCoreAsync(tx, moduleId, attributeId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task AssignAttributeCoreAsync(SqliteTransaction tx, ObjectId moduleId, ObjectId attributeId, CancellationToken cancellationToken)
    {
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        var next = Convert.ToInt32(await ScalarAsync(tx, "SELECT COALESCE(MAX(sort_order)+1,0) FROM module_attributes WHERE module_id=$m", cancellationToken, ("$m", moduleId.ToString())), CultureInfo.InvariantCulture);
        await using (var c = Cmd(tx, "INSERT INTO module_attributes(module_id,attribute_id,sort_order) VALUES ($m,$a,$o)"))
        {
            c.Parameters.AddWithValue("$m", moduleId.ToString()); c.Parameters.AddWithValue("$a", attributeId.ToString()); c.Parameters.AddWithValue("$o", next);
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "module.attribute_assigned", "container", moduleId.ToString(), $"attribute {attributeId}", cancellationToken);
    }

    public async Task<IReadOnlyList<AttributeDefinition>> ListModuleAttributesAsync(ObjectId moduleId, CancellationToken cancellationToken = default) =>
        await ReadDefinitionsAsync("SELECT " + DefinitionColumns + " FROM module_attributes m JOIN attribute_definitions d ON d.id = m.attribute_id WHERE m.module_id=$m ORDER BY m.sort_order", ("$m", moduleId.ToString()), cancellationToken);

    public async Task AddToOutlineAsync(ObjectId moduleId, ObjectId objectId, ObjectId? parentId, bool isHeading, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        var container = await ScalarAsync(tx, "SELECT container_id FROM engineering_objects WHERE id=$o", cancellationToken, ("$o", objectId.ToString()));
        if (container is null or DBNull || container.ToString() != moduleId.ToString()) throw new InvalidOperationException("The object does not exist or does not belong to this module.");
        if (parentId is { } parent && Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM outline_entries WHERE object_id=$p AND module_id=$m", cancellationToken, ("$p", parent.ToString()), ("$m", moduleId.ToString())), CultureInfo.InvariantCulture) == 0)
            throw new InvalidOperationException("The parent is not in this module's outline.");
        var next = Convert.ToInt32(await ScalarAsync(tx, "SELECT COALESCE(MAX(sort_order)+1,0) FROM outline_entries WHERE module_id=$m AND parent_object_id IS $p", cancellationToken, ("$m", moduleId.ToString()), ("$p", parentId?.ToString())), CultureInfo.InvariantCulture);
        await using (var c = Cmd(tx, "INSERT INTO outline_entries(object_id,module_id,parent_object_id,sort_order,is_heading) VALUES ($o,$m,$p,$s,$h)"))
        {
            c.Parameters.AddWithValue("$o", objectId.ToString()); c.Parameters.AddWithValue("$m", moduleId.ToString());
            c.Parameters.AddWithValue("$p", DbId(parentId));
            c.Parameters.AddWithValue("$s", next); c.Parameters.AddWithValue("$h", isHeading ? 1 : 0);
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "outline.added", "object", objectId.ToString(), $"module {moduleId}, parent {parentId?.ToString() ?? "none"}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task ApplyOutlineMoveAsync(SqliteTransaction tx, ObjectId moduleId, ObjectId objectId, ObjectId? newParentId, int index, CancellationToken ct)
    {
        var before = await ReadOutlineAsync(moduleId, tx, ct);
        var after = Outline.Move(before, objectId, newParentId, index); // throws on cycles / foreign parents
        var old = before.ToDictionary(e => e.ObjectId);
        foreach (var e in after)
        {
            var o = old[e.ObjectId];
            if (o.ParentId == e.ParentId && o.SortOrder == e.SortOrder) continue;
            await using var c = Cmd(tx, "UPDATE outline_entries SET parent_object_id=$p, sort_order=$s WHERE object_id=$o");
            c.Parameters.AddWithValue("$p", DbId(e.ParentId));
            c.Parameters.AddWithValue("$s", e.SortOrder); c.Parameters.AddWithValue("$o", e.ObjectId.ToString());
            await c.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task MoveOutlineEntryAsync(ObjectId moduleId, ObjectId objectId, ObjectId? newParentId, int index, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await ApplyOutlineMoveAsync(tx, moduleId, objectId, newParentId, index, cancellationToken);
        await AuditAsync(tx, "outline.moved", "object", objectId.ToString(), $"module {moduleId}, parent {newParentId?.ToString() ?? "none"}, index {index}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutlineEntry>> GetOutlineAsync(ObjectId moduleId, CancellationToken cancellationToken = default) => await ReadOutlineAsync(moduleId, null, cancellationToken);

    public async Task SetAttributeValueAsync(ObjectId objectId, ObjectId attributeId, string? value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await SetAttributeValueCoreAsync(tx, objectId, attributeId, value, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task SetAttributeValueCoreAsync(SqliteTransaction tx, ObjectId objectId, ObjectId attributeId, string? value, CancellationToken cancellationToken)
    {
        var definition = (await ReadDefinitionsAsync("SELECT " + DefinitionColumns + " FROM attribute_definitions d WHERE d.id=$a", ("$a", attributeId.ToString()), cancellationToken, tx)).SingleOrDefault()
            ?? throw new InvalidOperationException("Unknown attribute.");
        var module = await ScalarAsync(tx, "SELECT container_id FROM engineering_objects WHERE id=$o", cancellationToken, ("$o", objectId.ToString()));
        if (module is null or DBNull) throw new InvalidOperationException("The object does not exist or is not in a module.");
        if (Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM module_attributes WHERE module_id=$m AND attribute_id=$a", cancellationToken, ("$m", module.ToString()), ("$a", attributeId.ToString())), CultureInfo.InvariantCulture) == 0)
            throw new InvalidOperationException($"Attribute '{definition.Name}' is not assigned to the object's module.");
        var previous = await ScalarAsync(tx, "SELECT value FROM attribute_values WHERE object_id=$o AND attribute_id=$a", cancellationToken, ("$o", objectId.ToString()), ("$a", attributeId.ToString()));
        if (string.IsNullOrWhiteSpace(value))
        {
            if (definition.IsRequired) throw new InvalidOperationException($"Attribute '{definition.Name}' is required and cannot be cleared.");
            if (previous is null) return;
            await using var del = Cmd(tx, "DELETE FROM attribute_values WHERE object_id=$o AND attribute_id=$a");
            del.Parameters.AddWithValue("$o", objectId.ToString()); del.Parameters.AddWithValue("$a", attributeId.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken);
            await AuditAsync(tx, "attribute.cleared", "object", objectId.ToString(), $"{definition.Name}: '{previous}' -> (none)", cancellationToken);
            await RecordRevisionAsync(tx, objectId, true, $"{definition.Name}: '{previous}' \u2192 '(none)'", cancellationToken);
        }
        else
        {
            if (!definition.TryNormalize(value, out var normalized, out var error)) throw new ArgumentException(error, nameof(value));
            await using (var up = Cmd(tx, "INSERT INTO attribute_values(object_id,attribute_id,value) VALUES ($o,$a,$v) ON CONFLICT(object_id,attribute_id) DO UPDATE SET value=excluded.value"))
            {
                up.Parameters.AddWithValue("$o", objectId.ToString()); up.Parameters.AddWithValue("$a", attributeId.ToString()); up.Parameters.AddWithValue("$v", normalized);
                await up.ExecuteNonQueryAsync(cancellationToken);
            }
            if (previous is not null && previous.ToString() == normalized) return; // unchanged: no revision, no audit noise
            await AuditAsync(tx, "attribute.set", "object", objectId.ToString(), $"{definition.Name}: '{previous}' -> '{normalized}'", cancellationToken);
            await RecordRevisionAsync(tx, objectId, true, $"{definition.Name}: '{previous ?? "(none)"}' \u2192 '{normalized}'", cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AttributeValue>> ListAttributeValuesAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var list = new List<AttributeValue>();
        await using var c = Cmd(null, "SELECT v.object_id,v.attribute_id,v.value FROM attribute_values v JOIN engineering_objects o ON o.id = v.object_id WHERE o.container_id=$m ORDER BY v.object_id, v.attribute_id");
        c.Parameters.AddWithValue("$m", moduleId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) list.Add(new AttributeValue(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), r.GetString(2)));
        return list;
    }

    public async Task<IReadOnlyList<MissingValue>> FindMissingRequiredValuesAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var list = new List<MissingValue>();
        await using var c = Cmd(null, @"SELECT o.id, a.id FROM engineering_objects o
JOIN module_attributes ma ON ma.module_id = o.container_id
JOIN attribute_definitions a ON a.id = ma.attribute_id
LEFT JOIN attribute_values v ON v.object_id = o.id AND v.attribute_id = a.id
WHERE o.container_id=$m AND o.deleted_utc IS NULL AND a.is_required = 1 AND v.object_id IS NULL
ORDER BY o.canonical_id, a.name COLLATE NOCASE");
        c.Parameters.AddWithValue("$m", moduleId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) list.Add(new MissingValue(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1))));
        return list;
    }

    public async Task<TypedRelationship> AddLinkAsync(ObjectId sourceId, ObjectId targetId, RelationshipType type, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var link = await AddLinkCoreAsync(tx, sourceId, targetId, type, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return link;
    }

    private async Task<TypedRelationship> AddLinkCoreAsync(SqliteTransaction tx, ObjectId sourceId, ObjectId targetId, RelationshipType type, CancellationToken cancellationToken)
    {
        var link = TypedRelationship.Create(sourceId, targetId, type);
        // Deleted objects cannot be linked: their links would be hidden at once and could resurface (and break the level rule) on restore.
        var sourceModule = await ScalarAsync(tx, "SELECT container_id FROM engineering_objects WHERE id=$o AND deleted_utc IS NULL", cancellationToken, ("$o", sourceId.ToString()));
        var targetModule = await ScalarAsync(tx, "SELECT container_id FROM engineering_objects WHERE id=$o AND deleted_utc IS NULL", cancellationToken, ("$o", targetId.ToString()));
        if (sourceModule is null or DBNull || targetModule is null or DBNull) throw new InvalidOperationException("Both objects must exist (not be deleted) and belong to modules.");
        var targetParent = await ScalarAsync(tx, $"SELECT parent_id FROM containers WHERE id=$m AND kind={(int)ContainerKind.Module}", cancellationToken, ("$m", targetModule.ToString()));
        var sourceIsModule = Convert.ToInt64(await ScalarAsync(tx, $"SELECT COUNT(*) FROM containers WHERE id=$m AND kind={(int)ContainerKind.Module}", cancellationToken, ("$m", sourceModule.ToString())), CultureInfo.InvariantCulture) == 1;
        if (!sourceIsModule || targetParent is null or DBNull || targetParent.ToString() != sourceModule.ToString())
            throw new InvalidOperationException("Links must go from an object in a module to an object in one of its direct child modules.");
        await using (var c = Cmd(tx, "INSERT INTO relationships(id,source_id,target_id,type,created_utc) VALUES ($id,$source,$target,$type,$created)"))
        {
            c.Parameters.AddWithValue("$id", link.Id.ToString()); c.Parameters.AddWithValue("$source", sourceId.ToString()); c.Parameters.AddWithValue("$target", targetId.ToString());
            c.Parameters.AddWithValue("$type", (int)type); c.Parameters.AddWithValue("$created", link.CreatedUtc.ToString("O"));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await RecordLinkReviewAsync(tx, link.Id, cancellationToken);
        await AuditAsync(tx, "link.created", "relationship", link.Id.ToString(), $"{type} {sourceId}->{targetId}", cancellationToken);
        return link;
    }

    /// <summary>Records that the link was reviewed at the CURRENT revisions of both ends. A link is suspect when either end has moved on since.</summary>
    private async Task RecordLinkReviewAsync(SqliteTransaction tx, ObjectId relationshipId, CancellationToken cancellationToken)
    {
        await using var c = Cmd(tx, "INSERT OR REPLACE INTO link_reviews(relationship_id, source_revision, target_revision, reviewed_utc, actor) SELECT r.id, so.revision, t.revision, $now, $actor FROM relationships r JOIN engineering_objects so ON so.id = r.source_id JOIN engineering_objects t ON t.id = r.target_id WHERE r.id = $id");
        c.Parameters.AddWithValue("$id", relationshipId.ToString());
        c.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        c.Parameters.AddWithValue("$actor", _actor);
        await c.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SuspectLink>> ListSuspectLinksAsync(ObjectId? moduleId, CancellationToken cancellationToken = default)
    {
        var list = new List<SuspectLink>();
        var filter = moduleId is null ? "" : " AND (so.container_id = $m OR t.container_id = $m)";
        await using var c = Cmd(null, $@"SELECT r.id, r.source_id, r.target_id, r.type, so.revision > lr.source_revision, t.revision > lr.target_revision
FROM relationships r JOIN link_reviews lr ON lr.relationship_id = r.id JOIN engineering_objects so ON so.id = r.source_id JOIN engineering_objects t ON t.id = r.target_id
WHERE (so.revision > lr.source_revision OR t.revision > lr.target_revision) AND so.deleted_utc IS NULL AND t.deleted_utc IS NULL{filter} ORDER BY r.id");
        if (moduleId is { } m) c.Parameters.AddWithValue("$m", m.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new SuspectLink(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), ObjectId.Parse(r.GetString(2)), (RelationshipType)r.GetInt32(3), r.GetInt64(4) != 0, r.GetInt64(5) != 0));
        return list;
    }

    public async Task ClearSuspectAsync(IReadOnlyCollection<ObjectId> relationshipIds, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in relationshipIds)
        {
            await RecordLinkReviewAsync(tx, id, cancellationToken);
            await AuditAsync(tx, "link.reviewed", "relationship", id.ToString(), "Suspect flag cleared", cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken = default)
    {
        var list = new List<AuditEntry>();
        await using var c = Cmd(null, "SELECT seq, utc, actor, action, entity_type, entity_id, detail FROM audit_events ORDER BY seq DESC LIMIT $n");
        c.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 5000));
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new AuditEntry(r.GetInt64(0), ParseTime(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    public async Task RemoveLinkAsync(ObjectId relationshipId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        string detail;
        await using (var read = Cmd(tx, "SELECT source_id, target_id, type FROM relationships WHERE id=$id"))
        {
            read.Parameters.AddWithValue("$id", relationshipId.ToString());
            await using var r = await read.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken)) throw new InvalidOperationException("The link does not exist.");
            detail = $"{(RelationshipType)r.GetInt32(2)} {r.GetString(0)}->{r.GetString(1)}";
        }
        await using (var del = Cmd(tx, "DELETE FROM relationships WHERE id=$id"))
        {
            del.Parameters.AddWithValue("$id", relationshipId.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var delReview = Cmd(tx, "DELETE FROM link_reviews WHERE relationship_id=$id"))
        {
            delReview.Parameters.AddWithValue("$id", relationshipId.ToString());
            await delReview.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "link.removed", "relationship", relationshipId.ToString(), detail, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
