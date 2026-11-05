using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Comments and attachments (schema v8, v9).</summary>
public sealed partial class SqliteProjectStore
{
    // ----- Comments and attachments (schema v8) -----

    public async Task<Comment> AddCommentAsync(ObjectId objectId, ObjectId? parentId, string body, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var text = Comment.Validate(body);
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var live = await ScalarAsync(tx, "SELECT COUNT(*) FROM engineering_objects WHERE id=$o AND deleted_utc IS NULL", cancellationToken, ("$o", objectId.ToString()));
        if (Convert.ToInt64(live, CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("The object does not exist.");
        if (parentId is { } pid)
        {
            await using var check = Cmd(tx, "SELECT object_id, parent_id FROM comments WHERE id=$p");
            check.Parameters.AddWithValue("$p", pid.ToString());
            await using var r = await check.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken) || r.GetString(0) != objectId.ToString() || !r.IsDBNull(1))
                throw new InvalidOperationException("A reply must answer a top-level comment on the same object.");
        }
        var comment = new Comment(ObjectId.New(), objectId, parentId, _actor, text, DateTimeOffset.UtcNow, false);
        await using (var c = Cmd(tx, "INSERT INTO comments(id,object_id,parent_id,author,body,created_utc) VALUES ($id,$o,$p,$a,$b,$t)"))
        {
            c.Parameters.AddWithValue("$id", comment.Id.ToString()); c.Parameters.AddWithValue("$o", objectId.ToString());
            c.Parameters.AddWithValue("$p", DbId(parentId));
            c.Parameters.AddWithValue("$a", comment.Author); c.Parameters.AddWithValue("$b", text); c.Parameters.AddWithValue("$t", comment.CreatedUtc.ToString("O"));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "comment.added", "object", objectId.ToString(), parentId is null ? "New thread" : "Reply", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return comment;
    }

    public async Task<IReadOnlyList<Comment>> ListCommentsAsync(ObjectId objectId, CancellationToken cancellationToken = default)
    {
        var list = new List<Comment>();
        await using var c = Cmd(null, @"SELECT c.id, c.parent_id, c.author, c.body, c.created_utc,
  COALESCE((SELECT e.resolved FROM comment_events e WHERE e.comment_id = c.id ORDER BY e.seq DESC LIMIT 1), 0)
FROM comments c WHERE c.object_id = $o ORDER BY c.created_utc, c.id");
        c.Parameters.AddWithValue("$o", objectId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new Comment(ObjectId.Parse(r.GetString(0)), objectId, r.IsDBNull(1) ? null : ObjectId.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), ParseTime(r.GetString(4)), r.GetInt64(5) != 0));
        return list;
    }

    public async Task SetThreadResolvedAsync(ObjectId threadId, bool resolved, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var root = await ScalarAsync(tx, "SELECT object_id FROM comments WHERE id=$c AND parent_id IS NULL", cancellationToken, ("$c", threadId.ToString()));
        if (root is null or DBNull) throw new InvalidOperationException("The comment thread does not exist.");
        await using (var c = Cmd(tx, "INSERT INTO comment_events(comment_id,resolved,actor,utc) VALUES ($c,$r,$a,$t)"))
        {
            c.Parameters.AddWithValue("$c", threadId.ToString()); c.Parameters.AddWithValue("$r", resolved ? 1 : 0);
            c.Parameters.AddWithValue("$a", _actor); c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, resolved ? "comment.resolved" : "comment.reopened", "object", root.ToString()!, threadId.ToString(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<ObjectId, int>> CountOpenThreadsAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<ObjectId, int>();
        await using var c = Cmd(null, @"SELECT c.object_id, COUNT(*) FROM comments c JOIN engineering_objects o ON o.id = c.object_id
WHERE c.parent_id IS NULL AND o.container_id = $m AND o.deleted_utc IS NULL
  AND COALESCE((SELECT e.resolved FROM comment_events e WHERE e.comment_id = c.id ORDER BY e.seq DESC LIMIT 1), 0) = 0
GROUP BY c.object_id");
        c.Parameters.AddWithValue("$m", moduleId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) result[ObjectId.Parse(r.GetString(0))] = (int)r.GetInt64(1);
        return result;
    }

    public async Task<ObjectAttachment> AddAttachmentAsync(ObjectId objectId, string attachmentId, string fileName, long sizeBytes, string sha256, ObjectId? commentId = null, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        var name = ObjectAttachment.CleanName(fileName);
        if (sizeBytes > ObjectAttachment.MaxBytes) throw new InvalidOperationException($"Attachments are limited to {ObjectAttachment.MaxBytes / (1024 * 1024)} MB.");
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        var live = await ScalarAsync(tx, "SELECT COUNT(*) FROM engineering_objects WHERE id=$o AND deleted_utc IS NULL", cancellationToken, ("$o", objectId.ToString()));
        if (Convert.ToInt64(live, CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("The object does not exist.");
        if (commentId is { } cid)
        {
            var own = await ScalarAsync(tx, "SELECT COUNT(*) FROM comments WHERE id=$c AND object_id=$o", cancellationToken, ("$c", cid.ToString()), ("$o", objectId.ToString()));
            if (Convert.ToInt64(own, CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("The comment does not belong to this object.");
        }
        var item = new ObjectAttachment(ObjectId.New(), objectId, attachmentId, name, sizeBytes, sha256, DateTimeOffset.UtcNow, _actor, commentId);
        await using (var c = Cmd(tx, "INSERT INTO object_attachments(id,object_id,attachment_id,file_name,size_bytes,sha256,added_utc,actor,comment_id) VALUES ($id,$o,$a,$n,$s,$h,$t,$actor,$cm)"))
        {
            c.Parameters.AddWithValue("$cm", DbId(commentId));
            c.Parameters.AddWithValue("$id", item.Id.ToString()); c.Parameters.AddWithValue("$o", objectId.ToString()); c.Parameters.AddWithValue("$a", attachmentId);
            c.Parameters.AddWithValue("$n", name); c.Parameters.AddWithValue("$s", sizeBytes); c.Parameters.AddWithValue("$h", sha256);
            c.Parameters.AddWithValue("$t", item.AddedUtc.ToString("O")); c.Parameters.AddWithValue("$actor", _actor);
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "attachment.added", "object", objectId.ToString(), $"{name} ({sizeBytes} bytes)", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<ObjectAttachment>> ListAttachmentsAsync(ObjectId objectId, CancellationToken cancellationToken = default)
    {
        var list = new List<ObjectAttachment>();
        await using var c = Cmd(null, "SELECT id, attachment_id, file_name, size_bytes, sha256, added_utc, actor, comment_id FROM object_attachments WHERE object_id=$o AND removed_utc IS NULL ORDER BY added_utc, id");
        c.Parameters.AddWithValue("$o", objectId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(new ObjectAttachment(ObjectId.Parse(r.GetString(0)), objectId, r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), ParseTime(r.GetString(5)), r.GetString(6), r.IsDBNull(7) ? null : ObjectId.Parse(r.GetString(7))));
        return list;
    }

    public async Task<IReadOnlySet<string>> ListReferencedAttachmentIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var c = Cmd(null, "SELECT DISTINCT attachment_id FROM object_attachments");
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) ids.Add(r.GetString(0));
        return ids;
    }

    public async Task RemoveAttachmentAsync(ObjectId attachmentRecordId, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        string detail;
        await using (var read = Cmd(tx, "SELECT object_id, file_name FROM object_attachments WHERE id=$id AND removed_utc IS NULL"))
        {
            read.Parameters.AddWithValue("$id", attachmentRecordId.ToString());
            await using var r = await read.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken)) throw new InvalidOperationException("The attachment does not exist.");
            detail = $"{r.GetString(1)} (object {r.GetString(0)})";
        }
        await using (var c = Cmd(tx, "UPDATE object_attachments SET removed_utc=$t WHERE id=$id"))
        {
            c.Parameters.AddWithValue("$id", attachmentRecordId.ToString()); c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await c.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(tx, "attachment.removed", "object", attachmentRecordId.ToString(), detail, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<ObjectId, int>> CountAttachmentsAsync(ObjectId moduleId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<ObjectId, int>();
        await using var c = Cmd(null, "SELECT a.object_id, COUNT(*) FROM object_attachments a JOIN engineering_objects o ON o.id = a.object_id WHERE a.removed_utc IS NULL AND o.container_id = $m AND o.deleted_utc IS NULL GROUP BY a.object_id");
        c.Parameters.AddWithValue("$m", moduleId.ToString());
        await using var r = await c.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) result[ObjectId.Parse(r.GetString(0))] = (int)r.GetInt64(1);
        return result;
    }
}
