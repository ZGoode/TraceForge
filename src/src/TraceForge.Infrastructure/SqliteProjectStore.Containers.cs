using System.Globalization;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Rename, reorder, re-parent and delete of folders and modules, and removing an attribute column from a module.
/// No schema change: the existing containers.sort_order and module_attributes columns are used as they are.</summary>
public sealed partial class SqliteProjectStore
{
    private sealed record ContainerRow(ObjectId Id, ObjectId? ParentId, ContainerKind Kind, string Name, int SortOrder, string? DeletedUtc = null);

    private async Task<List<ContainerRow>> ReadContainerRowsAsync(SqliteTransaction tx, CancellationToken ct)
    {
        var rows = new List<ContainerRow>();
        await using var c = Cmd(tx, "SELECT id,parent_id,kind,name,sort_order,deleted_utc FROM containers");
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new ContainerRow(ObjectId.Parse(r.GetString(0)), r.IsDBNull(1) ? null : ObjectId.Parse(r.GetString(1)), (ContainerKind)r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return rows;
    }

    /// <summary>Tree order of the children of one parent: sort_order, then name, then id (all-zero legacy projects therefore keep their alphabetical order).</summary>
    private static List<ContainerRow> OrderedSiblings(IEnumerable<ContainerRow> rows, ObjectId? parent, ObjectId? except = null) =>
        rows.Where(c => c.ParentId == parent && c.Id != except && c.DeletedUtc is null)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id.ToString(), StringComparer.Ordinal).ToList();

    public async Task RenameContainerAsync(ObjectId containerId, string name, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Container name is required.", nameof(name));
        name = name.Trim();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var current = await ScalarAsync(tx, "SELECT name FROM containers WHERE id=$c", cancellationToken, ("$c", containerId.ToString()))
            ?? throw new InvalidOperationException("The folder or module does not exist.");
        if (Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM containers WHERE id=$c AND deleted_utc IS NOT NULL", cancellationToken, ("$c", containerId.ToString())), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException("This item is deleted. Restore it before renaming it.");
        if (current.ToString() == name) return;
        // Same-level names must stay distinct (case-insensitive). Renaming only the letter case of the same name is allowed.
        if (Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM containers WHERE id<>$c AND deleted_utc IS NULL AND name=$n COLLATE NOCASE AND parent_id IS (SELECT parent_id FROM containers WHERE id=$c)", cancellationToken, ("$c", containerId.ToString()), ("$n", name)), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException($"There is already a folder or module named '{name}' at this level. Choose a different name.");
        await using (var up = Cmd(tx, "UPDATE containers SET name=$n WHERE id=$c"))
        {
            up.Parameters.AddWithValue("$n", name); up.Parameters.AddWithValue("$c", containerId.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "container.renamed", "container", containerId.ToString(), $"'{current}' -> '{name}'", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task MoveContainerAsync(ObjectId containerId, ObjectId? newParentId, int index, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var rows = await ReadContainerRowsAsync(tx, cancellationToken);
        var byId = rows.ToDictionary(c => c.Id);
        var me = byId.GetValueOrDefault(containerId) ?? throw new InvalidOperationException("The folder or module does not exist.");
        if (me.DeletedUtc is not null) throw new InvalidOperationException("This item is deleted. Restore it before moving it.");
        ContainerRow? newParent = null;
        if (newParentId is { } np)
        {
            newParent = byId.GetValueOrDefault(np) ?? throw new InvalidOperationException("The new parent does not exist.");
            if (newParent.DeletedUtc is not null) throw new InvalidOperationException("The new parent is deleted. Restore it first.");
            var seen = new HashSet<ObjectId>();
            for (ContainerRow? cursor = newParent; cursor is not null && seen.Add(cursor.Id); cursor = cursor.ParentId is { } above ? byId.GetValueOrDefault(above) : null)
                if (cursor.Id == containerId) throw new InvalidOperationException("A folder or module cannot be moved under itself or one of its own children.");
            if (me.Kind == ContainerKind.Folder && newParent.Kind == ContainerKind.Module) throw new InvalidOperationException("A folder cannot be placed inside a module.");
        }
        if (me.ParentId != newParentId && me.Kind == ContainerKind.Module)
        {
            // Links run from a module to its DIRECT child module, so links INTO this module must still come from its (new) parent module.
            var expectedParent = newParent is { Kind: ContainerKind.Module } ? newParent.Id.ToString() : null;
            var broken = Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM relationships r JOIN engineering_objects s ON s.id = r.source_id JOIN engineering_objects t ON t.id = r.target_id WHERE t.container_id=$m AND s.container_id IS NOT $p",
                cancellationToken, ("$m", containerId.ToString()), ("$p", expectedParent)), CultureInfo.InvariantCulture);
            if (broken > 0) throw new InvalidOperationException($"{me.Name} has {broken} link(s) from objects of its current parent module. Links must come from the direct parent module, so remove those links first (or move it somewhere that keeps the same parent).");
        }
        if (me.ParentId != newParentId && rows.Any(c => c.ParentId == newParentId && c.DeletedUtc is null && c.Id != containerId && string.Equals(c.Name, me.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The new location already has a folder or module named '{me.Name}'. Rename one of them first.");
        var siblings = OrderedSiblings(rows, newParentId, containerId);
        siblings.Insert(Math.Clamp(index, 0, siblings.Count), me);
        for (var i = 0; i < siblings.Count; i++)
        {
            var row = siblings[i];
            var isMe = row.Id == containerId;
            if (!isMe && row.SortOrder == i) continue;
            if (isMe && row.SortOrder == i && me.ParentId == newParentId) continue;
            await using var up = Cmd(tx, "UPDATE containers SET parent_id=$p, sort_order=$s WHERE id=$c");
            up.Parameters.AddWithValue("$p", isMe ? DbId(newParentId) : DbId(row.ParentId));
            up.Parameters.AddWithValue("$s", i); up.Parameters.AddWithValue("$c", row.Id.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "container.moved", "container", containerId.ToString(), $"{me.Name}: parent {newParentId?.ToString() ?? "none"}, position {Math.Clamp(index, 0, siblings.Count - 1)}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var row = (await ReadContainerRowsAsync(tx, cancellationToken)).FirstOrDefault(c => c.Id == containerId) ?? throw new InvalidOperationException("The folder or module does not exist.");
        var id = containerId.ToString();
        async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(tx, sql, cancellationToken, ("$c", id)), CultureInfo.InvariantCulture);
        var what = row.Kind == ContainerKind.Folder ? "folder" : "module";
        var children = await CountAsync("SELECT COUNT(*) FROM containers WHERE parent_id=$c");
        if (children > 0) throw new InvalidOperationException($"The {what} '{row.Name}' still contains {children} folder(s) or module(s). Move or delete those first.");
        var objects = await CountAsync("SELECT COUNT(*) FROM engineering_objects WHERE container_id=$c");
        if (objects > 0) throw new InvalidOperationException($"The module '{row.Name}' still holds {objects} object(s), including deleted ones, which stay in the project history and cannot be destroyed. Only an empty module can be deleted.");
        var baselines = await CountAsync("SELECT COUNT(*) FROM baselines WHERE module_id=$c");
        if (baselines > 0) throw new InvalidOperationException($"The module '{row.Name}' has {baselines} baseline(s). Baselines are frozen records, so the module cannot be deleted.");
        foreach (var sql in new[] { "DELETE FROM saved_views WHERE module_id=$c", "DELETE FROM module_attributes WHERE module_id=$c", "DELETE FROM containers WHERE id=$c" })
        {
            await using var del = Cmd(tx, sql);
            del.Parameters.AddWithValue("$c", id);
            await del.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "container.deleted", "container", id, $"{row.Kind} {row.Name}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>Marks a folder or module, and everything live inside it, as deleted. Nothing is destroyed: it stays visible when "show deleted" is on, and can be restored or purged.</summary>
    public async Task SoftDeleteContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var rows = await ReadContainerRowsAsync(tx, cancellationToken);
        var me = rows.FirstOrDefault(c => c.Id == containerId) ?? throw new InvalidOperationException("The folder or module does not exist.");
        if (me.DeletedUtc is not null) throw new InvalidOperationException("This item is already deleted.");
        var batch = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var ids = SubtreeIds(rows, containerId, liveOnly: true);
        foreach (var id in ids)
        {
            await using var up = Cmd(tx, "UPDATE containers SET deleted_utc=$t, delete_batch=$b WHERE id=$c");
            up.Parameters.AddWithValue("$t", now); up.Parameters.AddWithValue("$b", batch); up.Parameters.AddWithValue("$c", id.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "container.soft_deleted", "container", containerId.ToString(), $"{me.Kind} {me.Name} ({ids.Count} item(s))", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>Undoes a soft delete: the item and everything deleted together with it come back (the item goes last among its siblings).</summary>
    public async Task RestoreContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var rows = await ReadContainerRowsAsync(tx, cancellationToken);
        var me = rows.FirstOrDefault(c => c.Id == containerId) ?? throw new InvalidOperationException("The folder or module does not exist.");
        if (me.DeletedUtc is null) throw new InvalidOperationException("This item is not deleted.");
        if (me.ParentId is { } pid && rows.First(c => c.Id == pid).DeletedUtc is not null)
            throw new InvalidOperationException("Its parent folder or module is deleted too. Restore the parent first.");
        if (rows.Any(c => c.ParentId == me.ParentId && c.DeletedUtc is null && string.Equals(c.Name, me.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A folder or module named '{me.Name}' already exists at this level. Rename one of them (the live one) first.");
        var batch = await ScalarAsync(tx, "SELECT delete_batch FROM containers WHERE id=$c", cancellationToken, ("$c", containerId.ToString()));
        var next = Convert.ToInt32(await ScalarAsync(tx, "SELECT COALESCE(MAX(sort_order)+1,0) FROM containers WHERE parent_id IS $p AND deleted_utc IS NULL", cancellationToken, ("$p", me.ParentId?.ToString())), CultureInfo.InvariantCulture);
        await using (var up = Cmd(tx, "UPDATE containers SET deleted_utc=NULL, delete_batch=NULL WHERE (id=$c OR (delete_batch IS NOT NULL AND delete_batch=$b))"))
        {
            up.Parameters.AddWithValue("$c", containerId.ToString()); up.Parameters.AddWithValue("$b", batch is null or DBNull ? "" : batch.ToString()!);
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var up = Cmd(tx, "UPDATE containers SET sort_order=$s WHERE id=$c"))
        {
            up.Parameters.AddWithValue("$s", next); up.Parameters.AddWithValue("$c", containerId.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "container.restored", "container", containerId.ToString(), $"{me.Kind} {me.Name}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>Permanently destroys an ALREADY DELETED folder or module with everything in it (objects, links, history, comments, views).
    /// Refused when a baseline exists in it, because baselines are frozen records. This is the only place the append-only rules are bypassed.</summary>
    public async Task PurgeContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var rows = await ReadContainerRowsAsync(tx, cancellationToken);
        var me = rows.FirstOrDefault(c => c.Id == containerId) ?? throw new InvalidOperationException("The folder or module does not exist.");
        if (me.DeletedUtc is null) throw new InvalidOperationException("Only a deleted folder or module can be permanently deleted. Delete it first.");
        var ids = SubtreeIds(rows, containerId, liveOnly: false);
        var live = rows.Where(c => ids.Contains(c.Id) && c.DeletedUtc is null).Select(c => c.Name).ToList();
        if (live.Count > 0) throw new InvalidOperationException($"'{live[0]}' inside it is not deleted. Delete or move it first.");
        await ExecuteAsync("CREATE TEMP TABLE purge_c(id TEXT PRIMARY KEY); CREATE TEMP TABLE purge_o(id TEXT PRIMARY KEY);", tx, cancellationToken);
        try
        {
            foreach (var id in ids)
            {
                await using var ins = Cmd(tx, "INSERT INTO purge_c(id) VALUES($i)");
                ins.Parameters.AddWithValue("$i", id.ToString());
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }
            var baselines = Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM baselines WHERE module_id IN (SELECT id FROM purge_c)", cancellationToken), CultureInfo.InvariantCulture);
            if (baselines > 0) throw new InvalidOperationException($"'{me.Name}' contains {baselines} baseline(s). Baselines are frozen records, so it cannot be permanently deleted. Leave it deleted (hidden) instead.");
            await ExecuteAsync("INSERT INTO purge_o SELECT id FROM engineering_objects WHERE container_id IN (SELECT id FROM purge_c);", tx, cancellationToken);
            var objectCount = Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM purge_o", cancellationToken), CultureInfo.InvariantCulture);
            await ExecuteAsync("DROP TRIGGER IF EXISTS object_revisions_no_delete; DROP TRIGGER IF EXISTS comments_no_delete; DROP TRIGGER IF EXISTS comment_events_no_delete;", tx, cancellationToken);
            var sql = new List<string>
            {
                "DELETE FROM link_reviews WHERE relationship_id IN (SELECT id FROM relationships WHERE source_id IN (SELECT id FROM purge_o) OR target_id IN (SELECT id FROM purge_o))",
                "DELETE FROM relationships WHERE source_id IN (SELECT id FROM purge_o) OR target_id IN (SELECT id FROM purge_o)",
                "DELETE FROM comment_events WHERE comment_id IN (SELECT id FROM comments WHERE object_id IN (SELECT id FROM purge_o))",
                "DELETE FROM comments WHERE object_id IN (SELECT id FROM purge_o)",
                "DELETE FROM object_attachments WHERE object_id IN (SELECT id FROM purge_o)",
                "DELETE FROM attribute_values WHERE object_id IN (SELECT id FROM purge_o)",
                "DELETE FROM object_revisions WHERE object_id IN (SELECT id FROM purge_o)",
                "DELETE FROM outline_entries WHERE object_id IN (SELECT id FROM purge_o) OR module_id IN (SELECT id FROM purge_c)",
                "DELETE FROM engineering_objects WHERE id IN (SELECT id FROM purge_o)",
                "DELETE FROM saved_views WHERE module_id IN (SELECT id FROM purge_c)",
                "DELETE FROM module_attributes WHERE module_id IN (SELECT id FROM purge_c)",
                "DELETE FROM containers WHERE id IN (SELECT id FROM purge_c)",
            };
            foreach (var statement in sql)
            {
                await using var del = Cmd(tx, statement);
                await del.ExecuteNonQueryAsync(cancellationToken);
            }
            await ExecuteAsync(@"CREATE TRIGGER object_revisions_no_delete BEFORE DELETE ON object_revisions BEGIN SELECT RAISE(ABORT, 'object_revisions is append-only'); END;
CREATE TRIGGER comments_no_delete BEFORE DELETE ON comments BEGIN SELECT RAISE(ABORT, 'comments are append-only'); END;
CREATE TRIGGER comment_events_no_delete BEFORE DELETE ON comment_events BEGIN SELECT RAISE(ABORT, 'comment events are append-only'); END;", tx, cancellationToken);
            await AuditAsync(tx, "container.purged", "container", containerId.ToString(), $"{me.Kind} {me.Name}: {ids.Count} folder(s)/module(s), {objectCount} object(s) permanently deleted", cancellationToken);
            await ExecuteAsync("DROP TABLE purge_c; DROP TABLE purge_o;", tx, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken); // also rolls back the dropped triggers and temp tables
            throw;
        }
    }

    private static HashSet<ObjectId> SubtreeIds(List<ContainerRow> rows, ObjectId root, bool liveOnly)
    {
        var result = new HashSet<ObjectId> { root };
        var queue = new Queue<ObjectId>([root]);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in rows.Where(c => c.ParentId == parent && (!liveOnly || c.DeletedUtc is null)))
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
        }
        return result;
    }

    public async Task RemoveAttributeFromModuleAsync(ObjectId moduleId, ObjectId attributeId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        var name = await ScalarAsync(tx, "SELECT d.name FROM module_attributes m JOIN attribute_definitions d ON d.id = m.attribute_id WHERE m.module_id=$m AND m.attribute_id=$a", cancellationToken, ("$m", moduleId.ToString()), ("$a", attributeId.ToString()))
            ?? throw new InvalidOperationException("That attribute is not used by this module.");
        var values = Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM attribute_values v JOIN engineering_objects o ON o.id = v.object_id WHERE o.container_id=$m AND v.attribute_id=$a", cancellationToken, ("$m", moduleId.ToString()), ("$a", attributeId.ToString())), CultureInfo.InvariantCulture);
        if (values > 0) throw new InvalidOperationException($"'{name}' still has a value on {values} object(s) of this module (deleted objects included). Clear those values first, so no data is hidden.");
        await using (var del = Cmd(tx, "DELETE FROM module_attributes WHERE module_id=$m AND attribute_id=$a"))
        {
            del.Parameters.AddWithValue("$m", moduleId.ToString()); del.Parameters.AddWithValue("$a", attributeId.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "module.attribute_removed", "container", moduleId.ToString(), $"attribute {name}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
