namespace TraceForge.Infrastructure;
public sealed record ManagedAttachment(string Id, string RelativePath, string Sha256, long SizeBytes);
/// <summary>A file in a project's attachments folder that no attachment record uses.</summary>
public sealed record UnusedAttachmentFile(string RelativePath, long SizeBytes);
public sealed record ProjectManifest(int FormatVersion, int SchemaVersion, string ProjectId, string DisplayName, string Database, DateTimeOffset CreatedUtc, IReadOnlyList<ManagedAttachment>? Attachments = null)
{
    public const int CurrentFormatVersion = 1;
    /// <summary>Must equal SqliteProjectStore.LatestSchemaVersion (enforced by a test).</summary>
    public const int CurrentSchemaVersion = 10;
}
