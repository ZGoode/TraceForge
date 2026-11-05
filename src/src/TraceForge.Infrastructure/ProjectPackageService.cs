using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TraceForge.Application;

namespace TraceForge.Infrastructure;

public sealed class ProjectPackageService(IDiagnosticLog? log = null, string actor = "local") : IProjectPackageService
{
    private const string ManifestFile = "project.json";
    private const string ManifestTempFile = "project.json.tmp";
    private const string DatabaseFile = "project.db";
    private const string StagingSuffix = ".staging";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IDiagnosticLog _log = log ?? NullDiagnosticLog.Instance;

    // ---------- create / open ----------

    public async Task CreateAsync(string packagePath, string displayName, CancellationToken cancellationToken = default)
    {
        var cid = NewCorrelationId();
        var full = Path.GetFullPath(packagePath);
        var staging = full + ".creating-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Project name is required.", nameof(displayName));
            if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Project destination already exists.");
            // Build in a staging directory and publish with a single rename so an interrupted create never leaves a half-made project.
            Directory.CreateDirectory(Path.Combine(staging, "attachments"));
            var manifest = new ProjectManifest(ProjectManifest.CurrentFormatVersion, ProjectManifest.CurrentSchemaVersion, Guid.NewGuid().ToString("N"), displayName.Trim(), DatabaseFile, DateTimeOffset.UtcNow, []);
            await WriteManifestAsync(staging, manifest, cancellationToken);
            await using (var store = new SqliteProjectStore(Path.Combine(staging, DatabaseFile), actor: actor)) await store.InitializeAsync(cancellationToken);
            Directory.Move(staging, full);
            _log.Write("info", "package.created", $"Created project at {full}", cid);
        }
        catch (Exception ex) { TryDeleteDirectory(staging); _log.Write("error", "package.create_failed", $"{ex.GetType().Name}: {ex.Message}", cid); throw; }
    }

    public async Task<IProjectStore> OpenAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var cid = NewCorrelationId();
        try
        {
            var report = await CheckAsync(packagePath, cancellationToken);
            if (!report.IsValid) throw new InvalidDataException(string.Join("; ", report.Findings));
            var root = Path.GetFullPath(packagePath);
            var manifest = await ReadManifestAsync(root, cancellationToken) ?? throw new InvalidDataException("[manifest] Invalid project manifest.");
            var store = new SqliteProjectStore(DatabasePath(root, manifest), actor: actor);
            try
            {
                await store.InitializeAsync(cancellationToken); // migrates (after a pre-migration backup) when the schema is older
                if (manifest.SchemaVersion != SqliteProjectStore.LatestSchemaVersion)
                    await WriteManifestAsync(root, manifest with { SchemaVersion = SqliteProjectStore.LatestSchemaVersion }, cancellationToken);
            }
            catch { await store.DisposeAsync(); throw; }
            _log.Write("info", "package.opened", $"Opened project at {root} with {report.Warnings?.Count ?? 0} warning(s)", cid);
            return store;
        }
        catch (Exception ex) { _log.Write("error", "package.open_failed", $"{ex.GetType().Name}: {ex.Message}", cid); throw; }
    }

    // ---------- integrity ----------

    public async Task<IntegrityReport> CheckAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        var warnings = new List<string>();
        IntegrityReport Result() => new(findings.Count == 0, findings, warnings);

        var root = Path.GetFullPath(packagePath);
        if (!File.Exists(Path.Combine(root, ManifestFile))) { findings.Add("[manifest] Missing project manifest."); return Result(); }
        ProjectManifest? manifest;
        try { manifest = await ReadManifestAsync(root, cancellationToken); }
        catch (JsonException ex) { findings.Add($"[manifest] Invalid project manifest: {ex.Message}"); return Result(); }
        if (manifest is null) { findings.Add("[manifest] Invalid project manifest."); return Result(); }

        if (manifest.FormatVersion != ProjectManifest.CurrentFormatVersion || manifest.SchemaVersion > ProjectManifest.CurrentSchemaVersion)
        {
            findings.Add("[manifest] Unsupported project format or schema version; open it with a newer application (no changes were made).");
            return Result(); // never touch a database written by a newer version
        }

        var databaseRelative = string.IsNullOrWhiteSpace(manifest.Database) ? DatabaseFile : manifest.Database;
        if (!IsSafeRelativePath(databaseRelative)) findings.Add("[manifest] Manifest database path is unsafe.");
        else
        {
            var databasePath = Path.Combine(root, databaseRelative);
            if (!File.Exists(databasePath)) findings.Add("[database] Missing project database.");
            else
            {
                try
                {
                    await using var store = new SqliteProjectStore(databasePath, readOnly: true);
                    await store.InitializeAsync(cancellationToken);
                    var version = await store.GetSchemaVersionAsync(cancellationToken);
                    if (version < manifest.SchemaVersion) findings.Add($"[database] Database schema {version} is older than the manifest's schema {manifest.SchemaVersion}.");
                    else if (version > manifest.SchemaVersion) warnings.Add($"[recovery] Database schema {version} is newer than the manifest's {manifest.SchemaVersion} (interrupted migration); opening will reconcile the manifest.");
                    else if (version < SqliteProjectStore.LatestSchemaVersion) warnings.Add($"[migration] Database schema {version} will be migrated to {SqliteProjectStore.LatestSchemaVersion} on open, after an automatic backup.");
                    var storeReport = await store.CheckIntegrityAsync(cancellationToken);
                    findings.AddRange(storeReport.Findings);
                    warnings.AddRange(storeReport.Warnings ?? []);
                }
                catch (SqliteException ex) { findings.Add($"[database] {ex.Message}"); }
                catch (InvalidDataException ex) { findings.Add($"[database] {ex.Message}"); }
            }
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in manifest.Attachments ?? [])
        {
            if (!IsSafeRelativePath(attachment.RelativePath)) { findings.Add($"[attachment] Unsafe attachment path: {attachment.Id}"); continue; }
            known.Add(attachment.RelativePath.Replace('\\', '/'));
            var attachmentPath = Path.Combine(root, attachment.RelativePath);
            if (!File.Exists(attachmentPath)) { findings.Add($"[attachment] Missing managed attachment: {attachment.Id}"); continue; }
            if (new FileInfo(attachmentPath).Length != attachment.SizeBytes || !string.Equals(await HashFileAsync(attachmentPath, cancellationToken), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                findings.Add($"[attachment] Managed attachment hash mismatch: {attachment.Id}");
        }

        // Leftovers from interrupted writes are reported, never silently deleted.
        if (File.Exists(Path.Combine(root, ManifestTempFile))) warnings.Add($"[recovery] Interrupted manifest write leftover: {ManifestTempFile}");
        var attachmentsDirectory = Path.Combine(root, "attachments");
        if (Directory.Exists(attachmentsDirectory))
            foreach (var file in Directory.EnumerateFiles(attachmentsDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase)) warnings.Add($"[recovery] Interrupted attachment staging file: {relative}");
                else if (!known.Contains(relative)) warnings.Add($"[recovery] Unreferenced file in attachments (possible interrupted import): {relative}");
            }

        if (findings.Count > 0) _log.Write("warn", "package.check_failed", $"{findings.Count} finding(s) at {root}: {string.Join(" | ", findings)}");
        return Result();
    }

    /// <summary>Removes only provably-temporary leftovers (staging files, manifest temp). Unreferenced attachments need a user decision and are kept.</summary>
    public Task<int> CleanInterruptedArtifactsAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packagePath);
        var removed = 0;
        var temp = Path.Combine(root, ManifestTempFile);
        if (File.Exists(temp)) { File.Delete(temp); removed++; }
        var attachmentsDirectory = Path.Combine(root, "attachments");
        if (Directory.Exists(attachmentsDirectory))
            foreach (var file in Directory.EnumerateFiles(attachmentsDirectory, "*" + StagingSuffix, SearchOption.AllDirectories)) { File.Delete(file); removed++; }
        _log.Write("info", "package.cleaned", $"Removed {removed} interrupted artifact(s) at {root}");
        return Task.FromResult(removed);
    }

    // ---------- attachments ----------

    public async Task<ManagedAttachment> ImportManagedAttachmentAsync(string packagePath, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Attachment source was not found.", sourcePath);
        var root = Path.GetFullPath(packagePath);
        var manifest = await ReadManifestAsync(root, cancellationToken) ?? throw new InvalidDataException("Invalid project manifest.");
        var id = Guid.NewGuid().ToString("N");
        var relative = "attachments/" + id + Path.GetExtension(sourcePath);
        var finalPath = Path.Combine(root, relative);
        var staging = finalPath + StagingSuffix;
        var published = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "attachments"));
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true); // push to disk before the file becomes visible
            }
            var hash = await HashFileAsync(staging, cancellationToken);
            var size = new FileInfo(staging).Length;
            File.Move(staging, finalPath);
            published = true;
            var item = new ManagedAttachment(id, relative, hash, size);
            await WriteManifestAsync(root, manifest with { Attachments = [.. (manifest.Attachments ?? []), item] }, cancellationToken);
            _log.Write("info", "attachment.imported", $"Imported attachment {id} ({size} bytes)");
            return item;
        }
        catch
        {
            // Roll back: never leave a file the manifest does not reference after a failed import.
            if (published && File.Exists(finalPath)) File.Delete(finalPath);
            throw;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    // ---------- unused attachment files ----------

    /// <summary>Files in attachments/ that no attachment record refers to: leftovers of Delete Permanently or of interrupted imports.
    /// <paramref name="referencedIds"/> comes from <see cref="IProjectStore.ListReferencedAttachmentIdsAsync"/>. Files of attachments the user merely removed are still referenced, so they are not listed.</summary>
    public Task<IReadOnlyList<UnusedAttachmentFile>> FindUnusedAttachmentsAsync(string packagePath, IReadOnlySet<string> referencedIds, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packagePath);
        var directory = Path.Combine(root, "attachments");
        var result = new List<UnusedAttachmentFile>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase) || !referencedIds.Contains(name.Split('.')[0]))
                    result.Add(new UnusedAttachmentFile(Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length));
            }
        return Task.FromResult<IReadOnlyList<UnusedAttachmentFile>>(result);
    }

    /// <summary>Deletes the files <see cref="FindUnusedAttachmentsAsync"/> reports and drops their manifest entries. Returns how many files were deleted.</summary>
    public async Task<int> DeleteUnusedAttachmentsAsync(string packagePath, IReadOnlySet<string> referencedIds, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packagePath);
        var unused = await FindUnusedAttachmentsAsync(root, referencedIds, cancellationToken);
        var manifest = await ReadManifestAsync(root, cancellationToken) ?? throw new InvalidDataException("[manifest] Invalid project manifest.");
        // Manifest first: if this is interrupted, the result is a harmless unreferenced file, never a manifest entry whose file is gone.
        var kept = (manifest.Attachments ?? []).Where(a => referencedIds.Contains(a.Id)).ToList();
        if (kept.Count != (manifest.Attachments?.Count ?? 0)) await WriteManifestAsync(root, manifest with { Attachments = kept }, cancellationToken);
        var attachmentsRoot = Path.GetFullPath(Path.Combine(root, "attachments")) + Path.DirectorySeparatorChar;
        var removed = 0;
        foreach (var item in unused)
        {
            var full = Path.GetFullPath(Path.Combine(root, item.RelativePath));
            if (!full.StartsWith(attachmentsRoot, PathComparison)) continue; // never delete anything outside attachments/
            File.Delete(full);
            removed++;
        }
        _log.Write("info", "attachment.cleaned", $"Deleted {removed} unused attachment file(s) at {root}");
        return removed;
    }

    // ---------- backup / restore ----------

    public async Task CreateBackupAsync(string packagePath, string archivePath, CancellationToken cancellationToken = default)
    {
        var cid = NewCorrelationId();
        var root = Path.GetFullPath(packagePath);
        var archive = Path.GetFullPath(archivePath);
        var temporary = archive + ".partial";
        try
        {
            if (archive.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) throw new ArgumentException("Backup archive must be outside the project package.");
            var report = await CheckAsync(root, cancellationToken);
            if (!report.IsValid) throw new InvalidDataException(string.Join("; ", report.Findings));
            if (File.Exists(archive) || Directory.Exists(archive)) throw new IOException("Backup destination already exists.");
            if (File.Exists(temporary)) File.Delete(temporary);
            var manifest = await ReadManifestAsync(root, cancellationToken) ?? throw new InvalidDataException("[manifest] Invalid project manifest.");
            var databaseRelative = (string.IsNullOrWhiteSpace(manifest.Database) ? DatabaseFile : manifest.Database).Replace('\\', '/');
            // The database is copied through SQLite (VACUUM INTO), never as a raw file: while a project is open its newest commits may still sit in the
            // -wal file, which a plain file copy would miss (or fail on, because the open application holds it).
            var snapshot = Path.Combine(Path.GetTempPath(), "traceforge-backup-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                await SnapshotDatabaseAsync(DatabasePath(root, manifest), snapshot, cancellationToken);
                using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                        if (IsBackupExcluded(relative) || IsDatabaseFile(relative, databaseRelative)) continue;
                        zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    }
                    zip.CreateEntryFromFile(snapshot, databaseRelative, CompressionLevel.Optimal);
                }
            }
            finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
            using (var zip = ZipFile.OpenRead(temporary))
            {
                ValidateArchiveEntries(zip);
                if (zip.GetEntry(ManifestFile) is null || zip.GetEntry(databaseRelative) is null) throw new InvalidDataException("Backup validation failed: manifest or database missing.");
            }
            File.Move(temporary, archive);
            _log.Write("info", "backup.created", $"Backup of {root} written to {archive}", cid);
        }
        catch (Exception ex) { _log.Write("error", "backup.failed", $"{ex.GetType().Name}: {ex.Message}", cid); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task RestoreBackupAsync(string archivePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var cid = NewCorrelationId();
        var archive = Path.GetFullPath(archivePath);
        var destination = Path.GetFullPath(destinationPath);
        var staging = destination + ".restoring-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Restore destination already exists.");
            if (!File.Exists(archive)) throw new FileNotFoundException("Backup archive was not found.", archive);
            using (var zip = ZipFile.OpenRead(archive)) ValidateArchiveEntries(zip);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Extract and verify in a separate directory; the destination only appears once the restored project is known good.
            ZipFile.ExtractToDirectory(archive, staging);
            var report = await CheckAsync(staging, cancellationToken);
            if (!report.IsValid) throw new InvalidDataException(string.Join("; ", report.Findings));
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Restore destination already exists.");
            Directory.Move(staging, destination);
            _log.Write("info", "restore.completed", $"Restored {archive} to {destination}", cid);
        }
        catch (Exception ex) { TryDeleteDirectory(staging); _log.Write("error", "restore.failed", $"{ex.GetType().Name}: {ex.Message}", cid); throw; }
    }

    // ---------- helpers ----------

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string NewCorrelationId() => Guid.NewGuid().ToString("N")[..12];
    private static string DatabasePath(string root, ProjectManifest manifest) => Path.Combine(root, string.IsNullOrWhiteSpace(manifest.Database) ? DatabaseFile : manifest.Database);
    /// <summary>The database file itself and its SQLite side files (write-ahead log, shared memory, rollback journal).</summary>
    private static bool IsDatabaseFile(string relative, string databaseRelative) =>
        relative.Equals(databaseRelative, StringComparison.OrdinalIgnoreCase) || relative.Equals(databaseRelative + "-wal", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(databaseRelative + "-shm", StringComparison.OrdinalIgnoreCase) || relative.Equals(databaseRelative + "-journal", StringComparison.OrdinalIgnoreCase);

    private static async Task SnapshotDatabaseAsync(string databasePath, string target, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}'";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool IsBackupExcluded(string relative) => relative.StartsWith("backups/", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase) || relative.Equals(ManifestTempFile, StringComparison.OrdinalIgnoreCase);

    private static void ValidateArchiveEntries(ZipArchive zip)
    {
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0) continue;
            if (Path.IsPathRooted(name) || name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':') || name.Split('/', '\\').Any(part => part == ".."))
                throw new InvalidDataException("Archive contains an unsafe entry path.");
        }
    }

    private static async Task<ProjectManifest?> ReadManifestAsync(string root, CancellationToken ct) =>
        JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllTextAsync(Path.Combine(root, ManifestFile), ct));

    /// <summary>Write-to-temp, flush, then replace, so an interruption leaves either the old or the new manifest intact.</summary>
    private static async Task WriteManifestAsync(string root, ProjectManifest manifest, CancellationToken ct)
    {
        var temp = Path.Combine(root, ManifestTempFile);
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        File.Move(temp, Path.Combine(root, ManifestFile), overwrite: true);
    }

    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static bool IsSafeRelativePath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Split('/', '\\').Any(s => s is "" or "." or "..");
    private static async Task<string> HashFileAsync(string path, CancellationToken ct) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
}
