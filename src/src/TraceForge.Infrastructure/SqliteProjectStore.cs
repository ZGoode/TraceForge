using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

public sealed partial class SqliteProjectStore : IProjectStore
{
    // Links whose ends are both live (a deleted object hides its links until it is restored).
    // Objects of a deleted (red) module stay live and editable so links can be fixed before the module is purged.
    private const string HiddenObjects = "SELECT id FROM engineering_objects WHERE deleted_utc IS NOT NULL";
    private const string LiveLinks = "source_id NOT IN (" + HiddenObjects + ") AND target_id NOT IN (" + HiddenObjects + ")";

    // Column list shared by every attribute-definition query (the table is always aliased as d); order matches ReadDefinitionsAsync.
    private const string DefinitionColumns = "d.id,d.name,d.data_type,d.is_required,d.enum_values,d.created_utc";


    private readonly string _databasePath;
    private readonly bool _readOnly;
    private readonly string _actor;
    private readonly SqliteConnection _connection;
    private bool _opened;

    // Project packages are movable files. Disable pooling so each completed operation
    // deterministically releases its database handles before backup, restore, or relocation.
    public SqliteProjectStore(string databasePath, bool readOnly = false, string actor = "local")
    {
        _databasePath = databasePath; _readOnly = readOnly; _actor = actor;
        _connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_opened) return;
        await _connection.OpenAsync(cancellationToken);
        _opened = true;
        await ExecuteAsync("PRAGMA foreign_keys = ON;", null, cancellationToken);
        if (_readOnly)
        {
            var version = await GetSchemaVersionAsync(cancellationToken);
            if (version > LatestSchemaVersion) throw new InvalidDataException($"Database schema {version} is newer than supported schema {LatestSchemaVersion}. Open it with a newer application.");
            return;
        }
        await ExecuteAsync("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;", null, cancellationToken);
        await MigrateAsync(cancellationToken);
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var exists = Cmd(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'");
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0) return 0;
        await using var command = Cmd(null, "SELECT COALESCE(MAX(version),0) FROM schema_migrations");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        var current = await GetSchemaVersionAsync(ct);
        if (current > LatestSchemaVersion) throw new InvalidDataException($"Database schema {current} is newer than supported schema {LatestSchemaVersion}. Open it with a newer application.");
        if (current == LatestSchemaVersion) return;
        if (current > 0) await BackupBeforeMigrationAsync(current, ct);
        foreach (var (version, sql) in Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            await ExecuteAsync(sql, tx, ct);
            await using (var record = Cmd(tx, "INSERT INTO schema_migrations(version, applied_utc) VALUES ($v, $t)"))
            {
                record.Parameters.AddWithValue("$v", version);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                await record.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private async Task BackupBeforeMigrationAsync(int fromVersion, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!, "backups");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"premigrate-v{fromVersion}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak");
        // VACUUM INTO produces a consistent, defragmented copy even while the database is in WAL mode.
        await ExecuteAsync($"VACUUM INTO '{target.Replace("'", "''")}'", null, ct);
    }

    public async Task AddContainerAsync(Container value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        if (value.ParentId is { } parentId)
        {
            var parentKind = await ScalarAsync(tx, "SELECT kind FROM containers WHERE id=$p AND deleted_utc IS NULL", cancellationToken, ("$p", parentId.ToString()))
                ?? throw new InvalidOperationException("The parent folder or module does not exist (or is deleted).");
            if (value.Kind == ContainerKind.Folder && Convert.ToInt32(parentKind, CultureInfo.InvariantCulture) == (int)ContainerKind.Module)
                throw new InvalidOperationException("A folder cannot be created inside a module.");
        }
        if (Convert.ToInt64(await ScalarAsync(tx, "SELECT COUNT(*) FROM containers WHERE parent_id IS $p AND deleted_utc IS NULL AND name=$n COLLATE NOCASE", cancellationToken, ("$p", value.ParentId?.ToString()), ("$n", value.Name)), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException($"There is already a folder or module named '{value.Name}' at this level. Choose a different name.");
        // New containers go last among their siblings (an explicit non-zero sort order is kept), so the tree order the user arranged is never disturbed.
        var order = value.SortOrder != 0 ? value.SortOrder
            : Convert.ToInt32(await ScalarAsync(tx, "SELECT COALESCE(MAX(sort_order)+1,0) FROM containers WHERE parent_id IS $p", cancellationToken, ("$p", value.ParentId?.ToString())), CultureInfo.InvariantCulture);
        await using (var c = Cmd(tx, "INSERT INTO containers(id,parent_id,kind,name,sort_order,created_utc) VALUES ($id,$parent,$kind,$name,$order,$created)"))
        {
            c.Parameters.AddWithValue("$id", value.Id.ToString());
            c.Parameters.AddWithValue("$parent", DbId(value.ParentId));
            c.Parameters.AddWithValue("$kind", (int)value.Kind);
            c.Parameters.AddWithValue("$name", value.Name);
            c.Parameters.AddWithValue("$order", order);
            c.Parameters.AddWithValue("$created", value.CreatedUtc.ToString("O"));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "container.created", "container", value.Id.ToString(), $"{value.Kind} {value.Name}", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public Task AddObjectAsync(EngineeringObject value, CancellationToken cancellationToken = default) => AddBatchAsync([value], [], cancellationToken);
    public Task AddRelationshipAsync(TypedRelationship value, CancellationToken cancellationToken = default) => AddBatchAsync([], [value], cancellationToken);

    private async Task InsertObjectAsync(SqliteTransaction tx, EngineeringObject o, CancellationToken ct)
    {
        await using var c = Cmd(tx, "INSERT INTO engineering_objects(id,type,canonical_id,title,revision,created_utc,modified_utc,container_id) VALUES ($id,$type,$canonical,$title,$revision,$created,$modified,$container)");
        c.Parameters.AddWithValue("$id", o.Id.ToString());
        c.Parameters.AddWithValue("$type", (int)o.Type);
        c.Parameters.AddWithValue("$canonical", o.CanonicalId);
        c.Parameters.AddWithValue("$title", o.Title);
        c.Parameters.AddWithValue("$revision", o.Revision);
        c.Parameters.AddWithValue("$created", o.CreatedUtc.ToString("O"));
        c.Parameters.AddWithValue("$modified", o.ModifiedUtc.ToString("O"));
        c.Parameters.AddWithValue("$container", DbId(o.ContainerId));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task AddBatchAsync(IReadOnlyCollection<EngineeringObject> objects, IReadOnlyCollection<TypedRelationship> relationships, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        // Disposing the transaction without Commit rolls everything back, so a failure leaves no partial objects or links.
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        foreach (var o in objects)
        {
            await InsertObjectAsync(tx, o, cancellationToken);
            await AuditAsync(tx, "object.created", "object", o.Id.ToString(), $"{o.Type} {o.CanonicalId}", cancellationToken);
            await RecordRevisionAsync(tx, o.Id, false, "Created", cancellationToken);
        }
        foreach (var r in relationships)
        {
            await using (var c = Cmd(tx, "INSERT INTO relationships(id,source_id,target_id,type,created_utc) VALUES ($id,$source,$target,$type,$created)"))
            {
                c.Parameters.AddWithValue("$id", r.Id.ToString());
                c.Parameters.AddWithValue("$source", r.SourceId.ToString());
                c.Parameters.AddWithValue("$target", r.TargetId.ToString());
                c.Parameters.AddWithValue("$type", (int)r.Type);
                c.Parameters.AddWithValue("$created", r.CreatedUtc.ToString("O"));
                await c.ExecuteNonQueryAsync(cancellationToken);
            }
            await RecordLinkReviewAsync(tx, r.Id, cancellationToken);
            await AuditAsync(tx, "relationship.created", "relationship", r.Id.ToString(), $"{r.Type} {r.SourceId}->{r.TargetId}", cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>Live folders and modules only. Deleted ones (restorable) come from <see cref="ListAllContainersAsync"/>.</summary>
    public Task<IReadOnlyList<Container>> ListContainersAsync(CancellationToken cancellationToken = default) => ReadContainersAsync(false, cancellationToken);

    /// <summary>Every folder and module, including deleted ones (see Container.DeletedUtc).</summary>
    public Task<IReadOnlyList<Container>> ListAllContainersAsync(CancellationToken cancellationToken = default) => ReadContainersAsync(true, cancellationToken);

    private async Task<IReadOnlyList<Container>> ReadContainersAsync(bool includeDeleted, CancellationToken cancellationToken)
    {
        var list = new List<Container>();
        await using var c = Cmd(null, "SELECT id,parent_id,kind,name,sort_order,created_utc,deleted_utc FROM containers" + (includeDeleted ? "" : " WHERE deleted_utc IS NULL") + " ORDER BY id");
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new Container(ObjectId.Parse(r.GetString(0)), r.IsDBNull(1) ? (ObjectId?)null : ObjectId.Parse(r.GetString(1)), (ContainerKind)r.GetInt32(2), r.GetString(3), r.GetInt32(4), ParseTime(r.GetString(5)),
                r.IsDBNull(6) ? (DateTimeOffset?)null : ParseTime(r.GetString(6))));
        return list;
    }

    /// <summary>All live objects, including those in deleted (red) modules.</summary>
    public Task<IReadOnlyList<EngineeringObject>> ListObjectsAsync(CancellationToken cancellationToken = default) => ReadObjectsAsync("", null, cancellationToken);

    /// <summary>Live objects of one module, also when the module itself is deleted (so a deleted module can still be looked at).</summary>
    public Task<IReadOnlyList<EngineeringObject>> ListModuleObjectsAsync(ObjectId moduleId, CancellationToken cancellationToken = default) =>
        ReadObjectsAsync(" AND container_id=$m", moduleId.ToString(), cancellationToken);

    private async Task<IReadOnlyList<EngineeringObject>> ReadObjectsAsync(string extraWhere, string? moduleArg, CancellationToken cancellationToken)
    {
        var list = new List<EngineeringObject>();
        await using var c = Cmd(null, "SELECT id,type,canonical_id,title,revision,created_utc,modified_utc,container_id FROM engineering_objects WHERE deleted_utc IS NULL" + extraWhere + " ORDER BY canonical_id");
        if (moduleArg is not null) c.Parameters.AddWithValue("$m", moduleArg);
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new EngineeringObject(ObjectId.Parse(r.GetString(0)), (EngineeringObjectType)r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt64(4), ParseTime(r.GetString(5)), ParseTime(r.GetString(6)), r.IsDBNull(7) ? (ObjectId?)null : ObjectId.Parse(r.GetString(7))));
        return list;
    }

    public async Task<IReadOnlyList<TypedRelationship>> ListRelationshipsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<TypedRelationship>();
        await using var c = Cmd(null, $"SELECT id,source_id,target_id,type,created_utc FROM relationships WHERE {LiveLinks} ORDER BY id");
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new TypedRelationship(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), ObjectId.Parse(r.GetString(2)), (RelationshipType)r.GetInt32(3), ParseTime(r.GetString(4))));
        return list;
    }

    public async Task<IntegrityReport> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        var warnings = new List<string>();

        var rows = new List<string>();
        await using (var c = Cmd(null, "PRAGMA integrity_check;"))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
            while (await r.ReadAsync(cancellationToken)) rows.Add(r.GetString(0));
        if (!(rows.Count == 1 && string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase)))
            findings.AddRange(rows.Count == 0 ? ["[database] SQLite integrity check returned no result."] : rows.Select(x => $"[database] {x}"));

        await using (var c = Cmd(null, "PRAGMA foreign_key_check;"))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
            while (await r.ReadAsync(cancellationToken))
            {
                var table = r.GetString(0);
                findings.Add($"[{(table == "relationships" ? "relationship" : "reference")}] Dangling reference in {table} (parent table {r.GetString(2)}).");
            }

        await using (var c = Cmd(null, $"SELECT COUNT(*) FROM relationships WHERE source_id = target_id AND type = {(int)RelationshipType.Decomposes}"))
        {
            var selfLinks = Convert.ToInt64(await c.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (selfLinks > 0) findings.Add($"[relationship] {selfLinks} object(s) decompose themselves.");
        }

        var schema = await GetSchemaVersionAsync(cancellationToken);
        if (schema >= 10)
        {
            await using var c = Cmd(null, "SELECT COUNT(*) FROM containers c JOIN containers p ON p.id = c.parent_id WHERE c.deleted_utc IS NULL AND p.deleted_utc IS NOT NULL");
            var orphans = Convert.ToInt64(await c.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (orphans > 0) findings.Add($"[container] {orphans} live folder(s) or module(s) sit inside a deleted one.");
        }
        // Older projects may hold two live siblings with the same name (case-insensitive). They still open and work; only creating, renaming or moving into such a clash is refused.
        await using (var c = Cmd(null, "SELECT MIN(c.name), COUNT(*), (SELECT p.name FROM containers p WHERE p.id = c.parent_id) FROM containers c " + (schema >= 10 ? "WHERE c.deleted_utc IS NULL " : "")
                                       + "GROUP BY c.parent_id, c.name COLLATE NOCASE HAVING COUNT(*) > 1"))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
            while (await r.ReadAsync(cancellationToken))
                warnings.Add($"[naming] {r.GetInt32(1)} folders or modules are named '{r.GetString(0)}' {(r.IsDBNull(2) ? "at the top level" : $"under '{r.GetString(2)}'")}. Rename all but one so names at one level stay distinct.");
        if (schema >= 3) await CheckModelingAsync(findings, cancellationToken);
        return new(findings.Count == 0, findings, warnings);
    }

    private async Task CheckModelingAsync(List<string> findings, CancellationToken ct)
    {
        foreach (var group in (await ReadOutlineAsync(null, null, ct)).GroupBy(e => e.ModuleId))
            findings.AddRange(Outline.Validate(group.ToList()).Select(p => $"[outline] Module {group.Key}: {p}"));
        await using (var c = Cmd(null, "SELECT COUNT(*) FROM outline_entries e JOIN outline_entries p ON e.parent_object_id = p.object_id WHERE e.module_id <> p.module_id"))
        {
            var crossModule = Convert.ToInt64(await c.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (crossModule > 0) findings.Add($"[outline] {crossModule} entr(ies) have a parent in a different module.");
        }
        await using (var c = Cmd(null, "SELECT COUNT(*) FROM outline_entries e JOIN engineering_objects o ON o.id = e.object_id WHERE o.container_id IS NOT e.module_id"))
        {
            var foreign = Convert.ToInt64(await c.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (foreign > 0) findings.Add($"[outline] {foreign} entr(ies) belong to an object that is not in the entry's module.");
        }
        if (await GetSchemaVersionAsync(ct) >= 5)
        {
            await using var history = Cmd(null, "SELECT COUNT(*) FROM engineering_objects o WHERE NOT EXISTS (SELECT 1 FROM object_revisions r WHERE r.object_id = o.id AND r.revision = o.revision)");
            var missingHistory = Convert.ToInt64(await history.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (missingHistory > 0) findings.Add($"[history] {missingHistory} object(s) have no revision record for their current revision.");
        }
        var definitions = (await ListAttributeDefinitionsAsync(ct)).ToDictionary(d => d.Id);
        await using var values = Cmd(null, "SELECT object_id, attribute_id, value FROM attribute_values");
        await using var r = await values.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var attributeId = ObjectId.Parse(r.GetString(1));
            var stored = r.GetString(2);
            if (!definitions.TryGetValue(attributeId, out var definition)) findings.Add($"[attribute] Value for unknown attribute {attributeId}.");
            else if (!definition.TryNormalize(stored, out var normalized, out var error) || normalized != stored) findings.Add($"[attribute] Invalid stored value for '{definition.Name}' on object {r.GetString(0)}{(error is null ? " (not canonical)" : ": " + error)}.");
        }
    }

    private async Task RequireModuleAsync(SqliteTransaction tx, ObjectId moduleId, CancellationToken ct)
    {
        var kind = await ScalarAsync(tx, "SELECT kind FROM containers WHERE id=$m", ct, ("$m", moduleId.ToString()));
        if (kind is null or DBNull || Convert.ToInt32(kind, CultureInfo.InvariantCulture) != (int)ContainerKind.Module) throw new InvalidOperationException("The container does not exist or is not a module.");
    }

    private async Task<object?> ScalarAsync(SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, string? Value)[] args)
    {
        await using var c = Cmd(tx, sql);
        foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value is null ? DBNull.Value : (object)value);
        return await c.ExecuteScalarAsync(ct);
    }

    private async Task<IReadOnlyList<OutlineEntry>> ReadOutlineAsync(ObjectId? moduleId, SqliteTransaction? tx, CancellationToken ct)
    {
        var list = new List<OutlineEntry>();
        await using var c = Cmd(tx, "SELECT e.object_id,e.module_id,e.parent_object_id,e.sort_order,e.is_heading FROM outline_entries e WHERE NOT EXISTS (SELECT 1 FROM engineering_objects o WHERE o.id = e.object_id AND o.deleted_utc IS NOT NULL)" + (moduleId is null ? "" : " AND e.module_id=$m") + " ORDER BY e.module_id, e.sort_order, e.object_id");
        if (moduleId is { } m) c.Parameters.AddWithValue("$m", m.ToString());
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new OutlineEntry(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), r.IsDBNull(2) ? (ObjectId?)null : ObjectId.Parse(r.GetString(2)), r.GetInt32(3), r.GetInt32(4) != 0));
        return list;
    }

    private async Task<IReadOnlyList<AttributeDefinition>> ReadDefinitionsAsync(string sql, (string Name, string Value)? arg, CancellationToken ct, SqliteTransaction? tx = null)
    {
        var list = new List<AttributeDefinition>();
        await using var c = Cmd(tx, sql);
        if (arg is { } a) c.Parameters.AddWithValue(a.Name, a.Value);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AttributeDefinition(ObjectId.Parse(r.GetString(0)), r.GetString(1), (AttributeDataType)r.GetInt32(2), r.GetInt32(3) != 0, JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [], ParseTime(r.GetString(5))));
        return list;
    }

    private async Task AuditAsync(SqliteTransaction tx, string action, string entityType, string entityId, string detail, CancellationToken ct)
    {
        await using var c = Cmd(tx, "INSERT INTO audit_events(utc,actor,action,entity_type,entity_id,detail) VALUES ($utc,$actor,$action,$type,$entity,$detail)");
        c.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        c.Parameters.AddWithValue("$actor", _actor);
        c.Parameters.AddWithValue("$action", action);
        c.Parameters.AddWithValue("$type", entityType);
        c.Parameters.AddWithValue("$entity", entityId);
        c.Parameters.AddWithValue("$detail", detail);
        await c.ExecuteNonQueryAsync(ct);
    }

    private static object DbId(ObjectId? id) => id is { } value ? (object)value.ToString() : DBNull.Value;
    private void EnsureWritable() { if (_readOnly) throw new InvalidOperationException("This project store was opened read-only."); }
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private SqliteCommand Cmd(SqliteTransaction? tx, string sql) { var c = _connection.CreateCommand(); c.Transaction = tx; c.CommandText = sql; return c; }
    private async Task ExecuteAsync(string sql, SqliteTransaction? tx, CancellationToken ct) { await using var c = Cmd(tx, sql); await c.ExecuteNonQueryAsync(ct); }
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
