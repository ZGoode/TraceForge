using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Object revisions, baselines and revision restore (schema v5).</summary>
public sealed partial class SqliteProjectStore
{
    // ---------- history and baselines (schema v5) ----------

    private async Task<string> ValuesJsonAsync(SqliteTransaction tx, ObjectId objectId, CancellationToken ct)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var c = Cmd(tx, "SELECT attribute_id, value FROM attribute_values WHERE object_id=$o");
        c.Parameters.AddWithValue("$o", objectId.ToString());
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) values[r.GetString(0)] = r.GetString(1);
        return JsonSerializer.Serialize(values);
    }

    /// <summary>Appends an immutable revision row. With bump the object's revision number is incremented first.</summary>
    private async Task RecordRevisionAsync(SqliteTransaction tx, ObjectId objectId, bool bump, string summary, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (bump)
        {
            await using var up = Cmd(tx, "UPDATE engineering_objects SET revision = revision + 1, modified_utc = $now WHERE id = $o");
            up.Parameters.AddWithValue("$now", now); up.Parameters.AddWithValue("$o", objectId.ToString());
            await up.ExecuteNonQueryAsync(ct);
        }
        string title; long revision;
        await using (var read = Cmd(tx, "SELECT title, revision FROM engineering_objects WHERE id = $o"))
        {
            read.Parameters.AddWithValue("$o", objectId.ToString());
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The object does not exist.");
            title = r.GetString(0); revision = r.GetInt64(1);
        }
        var json = await ValuesJsonAsync(tx, objectId, ct);
        await using var ins = Cmd(tx, "INSERT INTO object_revisions(object_id, revision, title, values_json, changed_utc, actor, summary) VALUES ($o,$rev,$title,$values,$utc,$actor,$summary)");
        ins.Parameters.AddWithValue("$o", objectId.ToString()); ins.Parameters.AddWithValue("$rev", revision); ins.Parameters.AddWithValue("$title", title);
        ins.Parameters.AddWithValue("$values", json); ins.Parameters.AddWithValue("$utc", now); ins.Parameters.AddWithValue("$actor", _actor); ins.Parameters.AddWithValue("$summary", summary);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public async Task RenameObjectAsync(ObjectId objectId, string title, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RenameObjectCoreAsync(tx, objectId, title, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task RenameObjectCoreAsync(SqliteTransaction tx, ObjectId objectId, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Text is required.", nameof(title));
        title = title.Trim();
        var current = await ScalarAsync(tx, "SELECT title FROM engineering_objects WHERE id=$o", cancellationToken, ("$o", objectId.ToString()))
            ?? throw new InvalidOperationException("The object does not exist.");
        if (current.ToString() == title) return;
        await using (var up = Cmd(tx, "UPDATE engineering_objects SET title=$t WHERE id=$o"))
        {
            up.Parameters.AddWithValue("$t", title); up.Parameters.AddWithValue("$o", objectId.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "object.renamed", "object", objectId.ToString(), $"'{current}' -> '{title}'", cancellationToken);
        await RecordRevisionAsync(tx, objectId, true, $"Text: '{current}' \u2192 '{title}'", cancellationToken);
    }

    public async Task<IReadOnlyList<ObjectRevision>> ListRevisionsAsync(ObjectId objectId, CancellationToken cancellationToken = default)
    {
        var list = new List<ObjectRevision>();
        await using var c = Cmd(null, "SELECT revision, title, values_json, changed_utc, actor, summary FROM object_revisions WHERE object_id=$o ORDER BY revision DESC");
        c.Parameters.AddWithValue("$o", objectId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new ObjectRevision(objectId, r.GetInt64(0), r.GetString(1), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? new(), ParseTime(r.GetString(3)), r.GetString(4), r.GetString(5)));
        return list;
    }

    private async Task<ModuleSnapshot> CaptureAsync(SqliteTransaction? tx, ObjectId moduleId, CancellationToken ct)
    {
        var outline = await ReadOutlineAsync(moduleId, tx, ct);
        var numbers = Outline.Number(outline);
        var headings = outline.ToDictionary(e => e.ObjectId, e => e.IsHeading);
        var values = new Dictionary<ObjectId, Dictionary<string, string>>();
        await using (var c = Cmd(tx, "SELECT v.object_id, v.attribute_id, v.value FROM attribute_values v JOIN engineering_objects o ON o.id = v.object_id WHERE o.container_id = $m AND o.deleted_utc IS NULL"))
        {
            c.Parameters.AddWithValue("$m", moduleId.ToString());
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = ObjectId.Parse(r.GetString(0));
                if (!values.TryGetValue(id, out var map)) values[id] = map = new Dictionary<string, string>();
                map[r.GetString(1)] = r.GetString(2);
            }
        }
        var objects = new List<BaselineObject>();
        await using (var c = Cmd(tx, "SELECT id, canonical_id, title FROM engineering_objects WHERE container_id = $m AND deleted_utc IS NULL ORDER BY canonical_id"))
        {
            c.Parameters.AddWithValue("$m", moduleId.ToString());
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = ObjectId.Parse(r.GetString(0));
                objects.Add(new BaselineObject(id, numbers.GetValueOrDefault(id, ""), r.GetString(1), r.GetString(2), headings.GetValueOrDefault(id), values.GetValueOrDefault(id) ?? new Dictionary<string, string>()));
            }
        }
        var links = new List<BaselineLink>();
        await using (var c = Cmd(tx, $"SELECT source_id, target_id, type FROM relationships WHERE (source_id IN (SELECT id FROM engineering_objects WHERE container_id = $m) OR target_id IN (SELECT id FROM engineering_objects WHERE container_id = $m)) AND {LiveLinks} ORDER BY source_id, target_id, type"))
        {
            c.Parameters.AddWithValue("$m", moduleId.ToString());
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) links.Add(new BaselineLink(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), (RelationshipType)r.GetInt32(2)));
        }
        return new ModuleSnapshot(objects, links);
    }

    public Task<ModuleSnapshot> CaptureModuleAsync(ObjectId moduleId, CancellationToken cancellationToken = default) => CaptureAsync(null, moduleId, cancellationToken);

    public async Task<Baseline> CreateBaselineAsync(ObjectId moduleId, string name, string? note, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A baseline name is required.", nameof(name));
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var baseline = await CreateBaselineCoreAsync(tx, moduleId, name.Trim(), note, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return baseline;
    }

    public async Task<IReadOnlyList<Baseline>> CreateBaselineSetAsync(ObjectId rootModuleId, string name, string? note, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A baseline name is required.", nameof(name));
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RequireModuleAsync(tx, rootModuleId, cancellationToken);
        // Same child order as the project tree (sort order, then name), so a set is created, listed and compared in the order the user sees.
        var moduleRows = (await ReadContainerRowsAsync(tx, cancellationToken)).Where(c => c.Kind == ContainerKind.Module).ToList();
        var modules = new List<ObjectId> { rootModuleId };
        for (var i = 0; i < modules.Count; i++)
            modules.AddRange(OrderedSiblings(moduleRows, modules[i]).Select(c => c.Id));
        var result = new List<Baseline>();
        foreach (var module in modules) result.Add(await CreateBaselineCoreAsync(tx, module, name.Trim(), note, cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<Baseline> CreateBaselineCoreAsync(SqliteTransaction tx, ObjectId moduleId, string name, string? note, CancellationToken cancellationToken)
    {
        await RequireModuleAsync(tx, moduleId, cancellationToken);
        if (Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM baselines WHERE module_id=$m AND name=$n COLLATE NOCASE", cancellationToken, ("$m", moduleId.ToString()), ("$n", name)), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException($"A baseline named '{name}' already exists for this module.");
        var baseline = new Baseline(ObjectId.New(), moduleId, name, (note ?? "").Trim(), DateTimeOffset.UtcNow);
        var content = await CaptureAsync(tx, moduleId, cancellationToken);
        await using (var c = Cmd(tx, "INSERT INTO baselines(id,module_id,name,note,created_utc,actor) VALUES ($id,$m,$n,$note,$utc,$actor)"))
        {
            c.Parameters.AddWithValue("$id", baseline.Id.ToString()); c.Parameters.AddWithValue("$m", moduleId.ToString()); c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$note", baseline.Note); c.Parameters.AddWithValue("$utc", baseline.CreatedUtc.ToString("O")); c.Parameters.AddWithValue("$actor", _actor);
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var o in content.Objects)
        {
            await using var c = Cmd(tx, "INSERT INTO baseline_objects(baseline_id,object_id,number,canonical_id,title,is_heading,values_json) VALUES ($b,$o,$num,$cid,$title,$h,$values)");
            c.Parameters.AddWithValue("$b", baseline.Id.ToString()); c.Parameters.AddWithValue("$o", o.ObjectId.ToString()); c.Parameters.AddWithValue("$num", o.Number);
            c.Parameters.AddWithValue("$cid", o.CanonicalId); c.Parameters.AddWithValue("$title", o.Title); c.Parameters.AddWithValue("$h", o.IsHeading ? 1 : 0);
            c.Parameters.AddWithValue("$values", JsonSerializer.Serialize(new SortedDictionary<string, string>(o.Values.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var l in content.Links)
        {
            await using var c = Cmd(tx, "INSERT INTO baseline_links(baseline_id,source_id,target_id,type) VALUES ($b,$s,$t,$type)");
            c.Parameters.AddWithValue("$b", baseline.Id.ToString()); c.Parameters.AddWithValue("$s", l.SourceId.ToString()); c.Parameters.AddWithValue("$t", l.TargetId.ToString()); c.Parameters.AddWithValue("$type", (int)l.Type);
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "baseline.created", "baseline", baseline.Id.ToString(), $"{name}: {content.Objects.Count} object(s), {content.Links.Count} link(s)", cancellationToken);
        return baseline;
    }

    private static Baseline ReadBaseline(SqliteDataReader r) => new(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), ParseTime(r.GetString(4)));

    public async Task<IReadOnlyList<Baseline>> ListBaselinesAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var list = new List<Baseline>();
        await using var c = Cmd(null, "SELECT id,module_id,name,note,created_utc FROM baselines WHERE module_id=$m ORDER BY created_utc, name");
        c.Parameters.AddWithValue("$m", moduleId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) list.Add(ReadBaseline(r));
        return list;
    }

    public async Task<BaselineSnapshot?> GetBaselineAsync(ObjectId baselineId, CancellationToken cancellationToken = default)
    {
        Baseline? baseline = null;
        await using (var c = Cmd(null, "SELECT id,module_id,name,note,created_utc FROM baselines WHERE id=$id"))
        {
            c.Parameters.AddWithValue("$id", baselineId.ToString());
            await using var r = await c.ExecuteReaderAsync(cancellationToken);
            if (await r.ReadAsync(cancellationToken)) baseline = ReadBaseline(r);
        }
        if (baseline is null) return null;
        var objects = new List<BaselineObject>();
        await using (var c = Cmd(null, "SELECT object_id,number,canonical_id,title,is_heading,values_json FROM baseline_objects WHERE baseline_id=$id ORDER BY canonical_id"))
        {
            c.Parameters.AddWithValue("$id", baselineId.ToString());
            await using var r = await c.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
                objects.Add(new BaselineObject(ObjectId.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0, JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5)) ?? new()));
        }
        var links = new List<BaselineLink>();
        await using (var c = Cmd(null, "SELECT source_id,target_id,type FROM baseline_links WHERE baseline_id=$id ORDER BY source_id,target_id,type"))
        {
            c.Parameters.AddWithValue("$id", baselineId.ToString());
            await using var r = await c.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) links.Add(new BaselineLink(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), (RelationshipType)r.GetInt32(2)));
        }
        return new BaselineSnapshot(baseline, new ModuleSnapshot(objects, links));
    }

    public async Task<bool> RestoreRevisionAsync(ObjectId objectId, long revision, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        string snapshotTitle; Dictionary<string, string> snapshot;
        await using (var read = Cmd(tx, "SELECT title, values_json FROM object_revisions WHERE object_id=$o AND revision=$r"))
        {
            read.Parameters.AddWithValue("$o", objectId.ToString()); read.Parameters.AddWithValue("$r", revision);
            await using var r = await read.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken)) throw new InvalidOperationException("That revision does not exist.");
            snapshotTitle = r.GetString(0);
            snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) ?? new();
        }
        var module = await ScalarAsync(tx, "SELECT container_id FROM engineering_objects WHERE id=$o AND deleted_utc IS NULL", cancellationToken, ("$o", objectId.ToString()));
        if (module is null or DBNull) throw new InvalidOperationException("The object does not exist or is deleted.");
        var currentTitle = (await ScalarAsync(tx, "SELECT title FROM engineering_objects WHERE id=$o", cancellationToken, ("$o", objectId.ToString())))!.ToString();
        var parts = new List<string>();
        if (currentTitle != snapshotTitle)
        {
            await using var up = Cmd(tx, "UPDATE engineering_objects SET title=$t WHERE id=$o");
            up.Parameters.AddWithValue("$t", snapshotTitle); up.Parameters.AddWithValue("$o", objectId.ToString());
            await up.ExecuteNonQueryAsync(cancellationToken);
            parts.Add("text");
        }
        var assigned = (await ReadDefinitionsAsync("SELECT " + DefinitionColumns + " FROM attribute_definitions d JOIN module_attributes ma ON ma.attribute_id = d.id WHERE ma.module_id=$m", ("$m", module.ToString()!), cancellationToken, tx)).ToList();
        var current = new Dictionary<string, string>();
        await using (var c = Cmd(tx, "SELECT attribute_id, value FROM attribute_values WHERE object_id=$o"))
        {
            c.Parameters.AddWithValue("$o", objectId.ToString());
            await using var r = await c.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) current[r.GetString(0)] = r.GetString(1);
        }
        foreach (var definition in assigned)
        {
            var key = definition.Id.ToString();
            snapshot.TryGetValue(key, out var want); current.TryGetValue(key, out var have);
            if (want is null)
            {
                if (have is null || definition.IsRequired) continue; // required values are never cleared
                await using var del = Cmd(tx, "DELETE FROM attribute_values WHERE object_id=$o AND attribute_id=$a");
                del.Parameters.AddWithValue("$o", objectId.ToString()); del.Parameters.AddWithValue("$a", key);
                await del.ExecuteNonQueryAsync(cancellationToken);
                parts.Add(definition.Name);
            }
            else if (want != have && definition.TryNormalize(want, out var normalized, out _))
            {
                await using var up = Cmd(tx, "INSERT INTO attribute_values(object_id,attribute_id,value) VALUES ($o,$a,$v) ON CONFLICT(object_id,attribute_id) DO UPDATE SET value=excluded.value");
                up.Parameters.AddWithValue("$o", objectId.ToString()); up.Parameters.AddWithValue("$a", key); up.Parameters.AddWithValue("$v", normalized);
                await up.ExecuteNonQueryAsync(cancellationToken);
                parts.Add(definition.Name);
            }
        }
        if (parts.Count == 0) return false;
        await AuditAsync(tx, "object.revision_restored", "object", objectId.ToString(), $"revision {revision}: {string.Join(", ", parts)}", cancellationToken);
        await RecordRevisionAsync(tx, objectId, true, $"Restored revision {revision} ({string.Join(", ", parts)})", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }
}
