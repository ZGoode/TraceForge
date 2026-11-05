using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Saved module views (schema v4).</summary>
public sealed partial class SqliteProjectStore
{
    public async Task SaveViewAsync(SavedView view, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (string.IsNullOrWhiteSpace(view.Name)) throw new ArgumentException("A view needs a name.", nameof(view));
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await RequireModuleAsync(tx, view.ModuleId, cancellationToken);
        await using (var del = Cmd(tx, "DELETE FROM saved_views WHERE module_id=$m AND name=$n COLLATE NOCASE"))
        {
            del.Parameters.AddWithValue("$m", view.ModuleId.ToString()); del.Parameters.AddWithValue("$n", view.Name.Trim());
            await del.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ins = Cmd(tx, "INSERT INTO saved_views(id,module_id,name,definition,created_utc) VALUES ($id,$m,$n,$d,$c)"))
        {
            ins.Parameters.AddWithValue("$id", view.Id.ToString()); ins.Parameters.AddWithValue("$m", view.ModuleId.ToString()); ins.Parameters.AddWithValue("$n", view.Name.Trim());
            ins.Parameters.AddWithValue("$d", JsonSerializer.Serialize(view.Definition)); ins.Parameters.AddWithValue("$c", view.CreatedUtc.ToString("O"));
            await ins.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "view.saved", "container", view.ModuleId.ToString(), view.Name.Trim(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SavedView>> ListViewsAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var list = new List<SavedView>();
        await using var c = Cmd(null, "SELECT id,module_id,name,definition,created_utc FROM saved_views WHERE module_id=$m ORDER BY name COLLATE NOCASE");
        c.Parameters.AddWithValue("$m", moduleId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new SavedView(ObjectId.Parse(r.GetString(0)), ObjectId.Parse(r.GetString(1)), r.GetString(2), JsonSerializer.Deserialize<ViewDefinition>(r.GetString(3)) ?? ViewDefinition.Default, ParseTime(r.GetString(4))));
        return list;
    }

    public async Task DeleteViewAsync(ObjectId viewId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await using (var del = Cmd(tx, "DELETE FROM saved_views WHERE id=$id"))
        {
            del.Parameters.AddWithValue("$id", viewId.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "view.deleted", "view", viewId.ToString(), "", cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
