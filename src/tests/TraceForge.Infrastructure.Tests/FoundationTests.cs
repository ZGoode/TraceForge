using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;
using TraceForge.Domain;
using TraceForge.Infrastructure;
using Xunit;

namespace TraceForge.Infrastructure.Tests;

/// <summary>Batch 1 acceptance coverage: ATP-01..04, migration, transactions, relationship integrity, diagnostics.</summary>
public sealed class FoundationTests : TempDirectoryTestBase
{
    private string P(string name) => Path.Combine(_root, name);

    /// <summary>Adds one container, one object of every type, and three links. Returns what was written.</summary>
    private static async Task<(Container Module, List<EngineeringObject> Objects, List<TypedRelationship> Links)> PopulateAsync(IProjectStore store)
    {
        var module = Container.Create(ContainerKind.Module, "System");
        await store.AddContainerAsync(module);
        var objects = Enum.GetValues<EngineeringObjectType>().Select(t => EngineeringObject.Create(t, $"OBJ-{(int)t:000}", $"Title {t}", module.Id)).ToList();
        EngineeringObject Of(EngineeringObjectType t) => objects.Single(o => o.Type == t);
        var links = new List<TypedRelationship>
        {
            TypedRelationship.Create(Of(EngineeringObjectType.DesignElement).Id, Of(EngineeringObjectType.Requirement).Id, RelationshipType.Satisfies),
            TypedRelationship.Create(Of(EngineeringObjectType.ImplementationTask).Id, Of(EngineeringObjectType.DesignElement).Id, RelationshipType.Implements),
            TypedRelationship.Create(Of(EngineeringObjectType.VerificationCase).Id, Of(EngineeringObjectType.Requirement).Id, RelationshipType.Verifies),
        };
        await store.AddBatchAsync(objects, links);
        return (module, objects, links);
    }

    private static async Task<int> AuditCountAsync(string dbPath)
    {
        await using var raw = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM audit_events";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact] public void Manifest_and_store_agree_on_latest_schema_version() => Assert.Equal(ProjectManifest.CurrentSchemaVersion, SqliteProjectStore.LatestSchemaVersion);

    // ATP-01
    [Fact] public async Task Atp01_all_object_types_containers_and_links_survive_close_and_reopen()
    {
        var service = new ProjectPackageService(); var project = P("a.traceproj");
        await service.CreateAsync(project, "ATP-01");
        (Container Module, List<EngineeringObject> Objects, List<TypedRelationship> Links) written;
        await using (var store = await service.OpenAsync(project)) written = await PopulateAsync(store);
        await using var reopened = await service.OpenAsync(project);
        Assert.Equal(written.Objects.OrderBy(o => o.CanonicalId, StringComparer.Ordinal), await reopened.ListObjectsAsync());
        Assert.Equal(written.Links.OrderBy(l => l.Id.ToString(), StringComparer.Ordinal), await reopened.ListRelationshipsAsync());
        Assert.Equal(written.Module, Assert.Single(await reopened.ListContainersAsync()));
        Assert.Equal(Enum.GetValues<EngineeringObjectType>().Length, (await reopened.ListObjectsAsync()).Count);
    }

    // ATP-02
    [Fact] public async Task Atp02_backup_restores_to_new_path_with_matching_content_and_leaves_source_unchanged()
    {
        var service = new ProjectPackageService(); var project = P("b.traceproj"); var backup = P("b.tracebackup"); var restored = P("b-restored.traceproj");
        await service.CreateAsync(project, "ATP-02");
        var evidence = P("evidence.txt"); await File.WriteAllTextAsync(evidence, "evidence");
        await using (var store = await service.OpenAsync(project)) await PopulateAsync(store);
        await service.ImportManagedAttachmentAsync(project, evidence);
        var manifestBefore = await File.ReadAllTextAsync(Path.Combine(project, "project.json"));

        await service.CreateBackupAsync(project, backup);
        await service.RestoreBackupAsync(backup, restored);

        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(Path.Combine(project, "project.json")));
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(Path.Combine(restored, "project.json")));
        await using var a = await service.OpenAsync(project); await using var b = await service.OpenAsync(restored);
        Assert.Equal(await a.ListObjectsAsync(), await b.ListObjectsAsync());
        Assert.Equal(await a.ListRelationshipsAsync(), await b.ListRelationshipsAsync());
        Assert.True((await service.CheckAsync(restored)).IsValid);
    }

    [Fact] public async Task Backup_cannot_be_written_inside_the_project()
    {
        var service = new ProjectPackageService(); var project = P("inside.traceproj");
        await service.CreateAsync(project, "Inside");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateBackupAsync(project, Path.Combine(project, "x.tracebackup")));
    }

    // ATP-03
    [Fact] public async Task Atp03_failed_batch_commits_nothing()
    {
        var service = new ProjectPackageService(); var project = P("c.traceproj");
        await service.CreateAsync(project, "ATP-03");
        await using var store = await service.OpenAsync(project);
        var req = EngineeringObject.Create(EngineeringObjectType.Requirement, "R-1", "Req");
        var dangling = TypedRelationship.Create(req.Id, ObjectId.New(), RelationshipType.Satisfies);
        await Assert.ThrowsAsync<SqliteException>(() => store.AddBatchAsync([req], [dangling]));
        Assert.Empty(await store.ListObjectsAsync());
        Assert.Empty(await store.ListRelationshipsAsync());
        Assert.Equal(0, await AuditCountAsync(Path.Combine(project, "project.db")));
    }

    [Fact] public async Task Atp03_duplicate_canonical_id_and_duplicate_link_are_rejected_without_partial_state()
    {
        var service = new ProjectPackageService(); var project = P("d.traceproj");
        await service.CreateAsync(project, "Dup");
        await using var store = await service.OpenAsync(project);
        var a = EngineeringObject.Create(EngineeringObjectType.Requirement, "R-1", "A");
        var b = EngineeringObject.Create(EngineeringObjectType.Requirement, "R-2", "B");
        await store.AddBatchAsync([a, b], [TypedRelationship.Create(a.Id, b.Id, RelationshipType.DerivedFrom)]);
        await Assert.ThrowsAsync<SqliteException>(() => store.AddObjectAsync(EngineeringObject.Create(EngineeringObjectType.Goal, "R-1", "Clash")));
        await Assert.ThrowsAsync<SqliteException>(() => store.AddRelationshipAsync(TypedRelationship.Create(a.Id, b.Id, RelationshipType.DerivedFrom)));
        Assert.Equal(2, (await store.ListObjectsAsync()).Count);
        Assert.Single(await store.ListRelationshipsAsync());
    }

    [Fact] public async Task Atp03_interrupted_write_leftovers_are_reported_and_only_temporary_ones_are_cleaned()
    {
        var service = new ProjectPackageService(); var project = P("e.traceproj");
        await service.CreateAsync(project, "Leftovers");
        await File.WriteAllTextAsync(Path.Combine(project, "attachments", "x.bin.staging"), "partial");
        await File.WriteAllTextAsync(Path.Combine(project, "attachments", "orphan.bin"), "promoted but never referenced");
        await File.WriteAllTextAsync(Path.Combine(project, "project.json.tmp"), "{");

        var before = await service.CheckAsync(project);
        Assert.True(before.IsValid); // the committed state is still intact
        Assert.Equal(3, before.Warnings!.Count(w => w.StartsWith("[recovery]")));

        Assert.Equal(2, await service.CleanInterruptedArtifactsAsync(project));
        var after = await service.CheckAsync(project);
        Assert.True(after.IsValid);
        Assert.Single(after.Warnings!, w => w.Contains("orphan.bin")); // needs a user decision, so it is kept
        Assert.True(File.Exists(Path.Combine(project, "attachments", "orphan.bin")));
    }

    [Fact] public async Task Atp03_failed_attachment_import_changes_nothing()
    {
        var service = new ProjectPackageService(); var project = P("f.traceproj");
        await service.CreateAsync(project, "Import");
        var manifestBefore = await File.ReadAllTextAsync(Path.Combine(project, "project.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ImportManagedAttachmentAsync(project, P("missing.txt")));
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(Path.Combine(project, "project.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(project, "attachments")));
    }

    // ATP-04
    [Fact] public async Task Atp04_tampered_database_is_identified_and_refused()
    {
        var service = new ProjectPackageService(); var project = P("g.traceproj");
        await service.CreateAsync(project, "Tamper DB");
        await using (var store = await service.OpenAsync(project)) await PopulateAsync(store);
        await using (var fs = new FileStream(Path.Combine(project, "project.db"), FileMode.Open, FileAccess.Write)) await fs.WriteAsync(Enumerable.Repeat((byte)0xFF, 128).ToArray());
        var report = await service.CheckAsync(project);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, f => f.StartsWith("[database]"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(project));
    }

    [Fact] public async Task Atp04_tampered_attachment_is_categorised_and_refused()
    {
        var service = new ProjectPackageService(); var project = P("h.traceproj"); var source = P("e.txt");
        await File.WriteAllTextAsync(source, "good"); await service.CreateAsync(project, "Tamper attachment");
        var attachment = await service.ImportManagedAttachmentAsync(project, source);
        await File.WriteAllTextAsync(Path.Combine(project, attachment.RelativePath), "evil");
        var report = await service.CheckAsync(project);
        Assert.Contains(report.Findings, f => f.StartsWith("[attachment]") && f.Contains("hash mismatch"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(project));
    }

    [Fact] public async Task Atp04_newer_schema_is_refused_without_modifying_the_package()
    {
        var service = new ProjectPackageService(); var project = P("i.traceproj");
        await service.CreateAsync(project, "Newer");
        var manifestPath = Path.Combine(project, "project.json");
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllTextAsync(manifestPath))!;
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with { SchemaVersion = 99 }));
        var dbBefore = await File.ReadAllBytesAsync(Path.Combine(project, "project.db"));
        var report = await service.CheckAsync(project);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, f => f.StartsWith("[manifest]") && f.Contains("Unsupported"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(project));
        Assert.Equal(dbBefore, await File.ReadAllBytesAsync(Path.Combine(project, "project.db")));
    }

    [Fact] public async Task Atp04_truncated_backup_is_rejected_and_nothing_is_created()
    {
        var service = new ProjectPackageService(); var project = P("j.traceproj"); var backup = P("j.tracebackup"); var restored = P("j-restored.traceproj");
        await service.CreateAsync(project, "Truncated"); await service.CreateBackupAsync(project, backup);
        var bytes = await File.ReadAllBytesAsync(backup); await File.WriteAllBytesAsync(backup, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreBackupAsync(backup, restored));
        Assert.False(Directory.Exists(restored));
        Assert.Empty(Directory.GetDirectories(_root, "j-restored.traceproj.restoring-*"));
    }

    [Fact] public async Task Atp04_backup_with_altered_attachment_is_rejected_and_nothing_is_created()
    {
        var service = new ProjectPackageService(); var project = P("k.traceproj"); var backup = P("k.tracebackup"); var restored = P("k-restored.traceproj"); var source = P("k.txt");
        await File.WriteAllTextAsync(source, "good"); await service.CreateAsync(project, "Altered");
        var attachment = await service.ImportManagedAttachmentAsync(project, source); await service.CreateBackupAsync(project, backup);
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            zip.GetEntry(attachment.RelativePath)!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(attachment.RelativePath).Open()); writer.Write("tampered");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreBackupAsync(backup, restored));
        Assert.False(Directory.Exists(restored));
    }

    [Fact] public async Task Atp04_backup_with_path_traversal_entry_is_rejected()
    {
        var service = new ProjectPackageService(); var archive = P("evil.tracebackup"); var restored = P("evil-restored.traceproj");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../evil.txt").Open()); writer.Write("x"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreBackupAsync(archive, restored));
        Assert.False(File.Exists(P("evil.txt"))); Assert.False(Directory.Exists(restored));
    }

    // Relationship integrity
    [Fact] public async Task Dangling_and_self_decomposing_links_are_detected_by_the_integrity_check()
    {
        var service = new ProjectPackageService(); var project = P("l.traceproj"); var db = Path.Combine(project, "project.db");
        await service.CreateAsync(project, "Links");
        var req = EngineeringObject.Create(EngineeringObjectType.Requirement, "R-1", "Req");
        await using (var store = await service.OpenAsync(project)) await store.AddObjectAsync(req);
        await RawAsync(db, "PRAGMA foreign_keys = OFF",
            $"INSERT INTO relationships(id,source_id,target_id,type,created_utc) VALUES ('d1','{req.Id}','{Guid.NewGuid():N}',2,'2026-01-01T00:00:00.0000000+00:00')",
            $"INSERT INTO relationships(id,source_id,target_id,type,created_utc) VALUES ('d2','{req.Id}','{req.Id}',0,'2026-01-01T00:00:00.0000000+00:00')");
        var report = await service.CheckAsync(project);
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, f => f.StartsWith("[relationship]") && f.Contains("Dangling"));
        Assert.Contains(report.Findings, f => f.StartsWith("[relationship]") && f.Contains("decompose themselves"));
    }

    [Fact] public async Task Audit_events_are_recorded_and_are_append_only()
    {
        var service = new ProjectPackageService(actor: "tester"); var project = P("m.traceproj"); var db = Path.Combine(project, "project.db");
        await service.CreateAsync(project, "Audit");
        await using (var store = await service.OpenAsync(project)) await PopulateAsync(store);
        Assert.True(await AuditCountAsync(db) >= 1 + Enum.GetValues<EngineeringObjectType>().Length + 3);
        await Assert.ThrowsAsync<SqliteException>(() => RawAsync(db, "UPDATE audit_events SET actor='x'"));
        await Assert.ThrowsAsync<SqliteException>(() => RawAsync(db, "DELETE FROM audit_events"));
    }

    // Migration
    [Fact] public async Task Schema_v1_database_is_migrated_after_a_preflight_backup_without_losing_data()
    {
        var packageDir = P("n.traceproj"); Directory.CreateDirectory(packageDir); var db = Path.Combine(packageDir, "project.db");
        var id = Guid.NewGuid().ToString("N");
        await RawAsync(db,
            "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL)",
            "CREATE TABLE engineering_objects(id TEXT PRIMARY KEY, type INTEGER NOT NULL, canonical_id TEXT NOT NULL UNIQUE, title TEXT NOT NULL, revision INTEGER NOT NULL, created_utc TEXT NOT NULL, modified_utc TEXT NOT NULL)",
            "CREATE TABLE relationships(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES engineering_objects(id), target_id TEXT NOT NULL REFERENCES engineering_objects(id), type INTEGER NOT NULL, created_utc TEXT NOT NULL, UNIQUE(source_id,target_id,type))",
            "INSERT INTO schema_migrations VALUES (1,'2026-01-01T00:00:00.0000000+00:00')",
            $"INSERT INTO engineering_objects VALUES ('{id}',0,'REQ-1','Legacy',1,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00')");

        await using (var store = new SqliteProjectStore(db))
        {
            await store.InitializeAsync();
            Assert.Equal(SqliteProjectStore.LatestSchemaVersion, await store.GetSchemaVersionAsync());
            var legacy = Assert.Single(await store.ListObjectsAsync());
            Assert.Equal("Legacy", legacy.Title); Assert.Null(legacy.ContainerId);
        }
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(packageDir, "backups"), "premigrate-v1-*.bak"));
    }

    [Fact] public async Task Read_only_store_refuses_writes()
    {
        var service = new ProjectPackageService(); var project = P("o.traceproj");
        await service.CreateAsync(project, "RO");
        await using var store = new SqliteProjectStore(Path.Combine(project, "project.db"), readOnly: true);
        await store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddObjectAsync(EngineeringObject.Create(EngineeringObjectType.Goal, "G-1", "Goal")));
    }

    // Diagnostics
    [Fact] public async Task Diagnostics_log_records_package_events_as_json_lines()
    {
        var logPath = P("logs/diag.log"); var service = new ProjectPackageService(new FileDiagnosticLog(logPath));
        await service.CreateAsync(P("p.traceproj"), "Logged");
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(P("p.traceproj"), "Again"));
        var lines = await File.ReadAllLinesAsync(logPath);
        Assert.Contains(lines, l => l.Contains("package.created"));
        Assert.Contains(lines, l => l.Contains("package.create_failed"));
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
    }

    [Fact] public void Diagnostics_redacts_the_user_profile_path()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length < 2) return;
        Assert.DoesNotContain(home, FileDiagnosticLog.Redact(Path.Combine(home, "secret", "project.traceproj")));
    }
}
