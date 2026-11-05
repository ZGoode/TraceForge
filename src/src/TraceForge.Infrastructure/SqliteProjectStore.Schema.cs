using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;

namespace TraceForge.Infrastructure;

/// <summary>Schema migrations: the ordered SQL for every schema version.</summary>
public sealed partial class SqliteProjectStore
{
    private const string V1 = @"
CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
CREATE TABLE engineering_objects(id TEXT PRIMARY KEY, type INTEGER NOT NULL, canonical_id TEXT NOT NULL UNIQUE, title TEXT NOT NULL, revision INTEGER NOT NULL, created_utc TEXT NOT NULL, modified_utc TEXT NOT NULL);
CREATE TABLE relationships(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES engineering_objects(id), target_id TEXT NOT NULL REFERENCES engineering_objects(id), type INTEGER NOT NULL, created_utc TEXT NOT NULL, UNIQUE(source_id,target_id,type));";

    private const string V2 = @"
CREATE TABLE containers(id TEXT PRIMARY KEY, parent_id TEXT NULL REFERENCES containers(id), kind INTEGER NOT NULL, name TEXT NOT NULL, sort_order INTEGER NOT NULL, created_utc TEXT NOT NULL);
ALTER TABLE engineering_objects ADD COLUMN container_id TEXT NULL REFERENCES containers(id);
CREATE TABLE audit_events(seq INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, detail TEXT NOT NULL);
CREATE TRIGGER audit_events_no_update BEFORE UPDATE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit_events is append-only'); END;
CREATE TRIGGER audit_events_no_delete BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit_events is append-only'); END;";

    private const string V3 = @"
CREATE TABLE attribute_definitions(id TEXT PRIMARY KEY, name TEXT NOT NULL, data_type INTEGER NOT NULL, is_required INTEGER NOT NULL, enum_values TEXT NOT NULL, created_utc TEXT NOT NULL);
CREATE UNIQUE INDEX ux_attribute_definitions_name ON attribute_definitions(name COLLATE NOCASE);
CREATE TABLE module_attributes(module_id TEXT NOT NULL REFERENCES containers(id), attribute_id TEXT NOT NULL REFERENCES attribute_definitions(id), sort_order INTEGER NOT NULL, PRIMARY KEY(module_id, attribute_id));
CREATE TABLE outline_entries(object_id TEXT PRIMARY KEY REFERENCES engineering_objects(id), module_id TEXT NOT NULL REFERENCES containers(id), parent_object_id TEXT NULL REFERENCES engineering_objects(id), sort_order INTEGER NOT NULL, is_heading INTEGER NOT NULL);
CREATE INDEX ix_outline_entries_module ON outline_entries(module_id, parent_object_id, sort_order);
CREATE TABLE attribute_values(object_id TEXT NOT NULL REFERENCES engineering_objects(id), attribute_id TEXT NOT NULL REFERENCES attribute_definitions(id), value TEXT NOT NULL, PRIMARY KEY(object_id, attribute_id));";

    private const string V4 = @"
CREATE TABLE saved_views(id TEXT PRIMARY KEY, module_id TEXT NOT NULL REFERENCES containers(id), name TEXT NOT NULL, definition TEXT NOT NULL, created_utc TEXT NOT NULL);
CREATE UNIQUE INDEX ux_saved_views_name ON saved_views(module_id, name COLLATE NOCASE);";

    private const string V5 = @"
CREATE TABLE object_revisions(object_id TEXT NOT NULL REFERENCES engineering_objects(id), revision INTEGER NOT NULL, title TEXT NOT NULL, values_json TEXT NOT NULL, changed_utc TEXT NOT NULL, actor TEXT NOT NULL, summary TEXT NOT NULL, PRIMARY KEY(object_id, revision));
CREATE TRIGGER object_revisions_no_update BEFORE UPDATE ON object_revisions BEGIN SELECT RAISE(ABORT, 'object_revisions is append-only'); END;
CREATE TRIGGER object_revisions_no_delete BEFORE DELETE ON object_revisions BEGIN SELECT RAISE(ABORT, 'object_revisions is append-only'); END;
INSERT INTO object_revisions(object_id, revision, title, values_json, changed_utc, actor, summary)
  SELECT o.id, o.revision, o.title, COALESCE((SELECT json_group_object(v.attribute_id, v.value) FROM attribute_values v WHERE v.object_id = o.id), '{}'), o.modified_utc, 'migration', 'Snapshot at migration' FROM engineering_objects o;
CREATE TABLE baselines(id TEXT PRIMARY KEY, module_id TEXT NOT NULL REFERENCES containers(id), name TEXT NOT NULL, note TEXT NOT NULL, created_utc TEXT NOT NULL, actor TEXT NOT NULL);
CREATE UNIQUE INDEX ux_baselines_name ON baselines(module_id, name COLLATE NOCASE);
CREATE TABLE baseline_objects(baseline_id TEXT NOT NULL REFERENCES baselines(id), object_id TEXT NOT NULL, number TEXT NOT NULL, canonical_id TEXT NOT NULL, title TEXT NOT NULL, is_heading INTEGER NOT NULL, values_json TEXT NOT NULL, PRIMARY KEY(baseline_id, object_id));
CREATE TABLE baseline_links(baseline_id TEXT NOT NULL REFERENCES baselines(id), source_id TEXT NOT NULL, target_id TEXT NOT NULL, type INTEGER NOT NULL, PRIMARY KEY(baseline_id, source_id, target_id, type));
CREATE TRIGGER baselines_no_update BEFORE UPDATE ON baselines BEGIN SELECT RAISE(ABORT, 'baselines are frozen'); END;
CREATE TRIGGER baselines_no_delete BEFORE DELETE ON baselines BEGIN SELECT RAISE(ABORT, 'baselines are frozen'); END;
CREATE TRIGGER baseline_objects_no_update BEFORE UPDATE ON baseline_objects BEGIN SELECT RAISE(ABORT, 'baselines are frozen'); END;
CREATE TRIGGER baseline_objects_no_delete BEFORE DELETE ON baseline_objects BEGIN SELECT RAISE(ABORT, 'baselines are frozen'); END;
CREATE TRIGGER baseline_links_no_update BEFORE UPDATE ON baseline_links BEGIN SELECT RAISE(ABORT, 'baselines are frozen'); END;
CREATE TRIGGER baseline_links_no_delete BEFORE DELETE ON baseline_links BEGIN SELECT RAISE(ABORT, 'baselines are frozen'); END;";

    private const string V6 = @"
ALTER TABLE engineering_objects ADD COLUMN deleted_utc TEXT NULL;
ALTER TABLE engineering_objects ADD COLUMN delete_batch TEXT NULL;
CREATE INDEX ix_engineering_objects_batch ON engineering_objects(delete_batch);";

    private const string V7 = @"
CREATE TABLE link_reviews(relationship_id TEXT PRIMARY KEY, source_revision INTEGER NOT NULL, target_revision INTEGER NOT NULL, reviewed_utc TEXT NOT NULL, actor TEXT NOT NULL);
INSERT INTO link_reviews(relationship_id, source_revision, target_revision, reviewed_utc, actor)
SELECT r.id, so.revision, t.revision, strftime('%Y-%m-%dT%H:%M:%fZ','now'), 'migration'
FROM relationships r JOIN engineering_objects so ON so.id = r.source_id JOIN engineering_objects t ON t.id = r.target_id;";

    private const string V8 = @"
CREATE TABLE comments(id TEXT PRIMARY KEY, object_id TEXT NOT NULL REFERENCES engineering_objects(id), parent_id TEXT NULL REFERENCES comments(id), author TEXT NOT NULL, body TEXT NOT NULL, created_utc TEXT NOT NULL);
CREATE INDEX ix_comments_object ON comments(object_id);
CREATE TABLE comment_events(seq INTEGER PRIMARY KEY AUTOINCREMENT, comment_id TEXT NOT NULL REFERENCES comments(id), resolved INTEGER NOT NULL, actor TEXT NOT NULL, utc TEXT NOT NULL);
CREATE TABLE object_attachments(id TEXT PRIMARY KEY, object_id TEXT NOT NULL REFERENCES engineering_objects(id), attachment_id TEXT NOT NULL, file_name TEXT NOT NULL, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL, added_utc TEXT NOT NULL, actor TEXT NOT NULL, removed_utc TEXT NULL);
CREATE INDEX ix_object_attachments_object ON object_attachments(object_id);
CREATE TRIGGER comments_no_update BEFORE UPDATE ON comments BEGIN SELECT RAISE(ABORT, 'comments are append-only'); END;
CREATE TRIGGER comments_no_delete BEFORE DELETE ON comments BEGIN SELECT RAISE(ABORT, 'comments are append-only'); END;
CREATE TRIGGER comment_events_no_update BEFORE UPDATE ON comment_events BEGIN SELECT RAISE(ABORT, 'comment events are append-only'); END;
CREATE TRIGGER comment_events_no_delete BEFORE DELETE ON comment_events BEGIN SELECT RAISE(ABORT, 'comment events are append-only'); END;";

    private const string V9 = @"
ALTER TABLE object_attachments ADD COLUMN comment_id TEXT NULL;";

    // Folders and modules can be deleted without destroying anything: they are only marked (a whole subtree shares one delete_batch) and can be restored or purged later.
    private const string V10 = @"
ALTER TABLE containers ADD COLUMN deleted_utc TEXT NULL;
ALTER TABLE containers ADD COLUMN delete_batch TEXT NULL;";

    private static readonly (int Version, string Sql)[] Migrations = [(1, V1), (2, V2), (3, V3), (4, V4), (5, V5), (6, V6), (7, V7), (8, V8), (9, V9), (10, V10)];
    public static int LatestSchemaVersion => Migrations[^1].Version;
}
