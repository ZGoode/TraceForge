using TraceForge.Domain;
namespace TraceForge.Application;

public interface IProjectStore : IAsyncDisposable
{
    /// <summary>Opens the database. Writable stores migrate to the latest schema (with a pre-migration backup); read-only stores never write.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);
    Task AddContainerAsync(Container value, CancellationToken cancellationToken = default);
    /// <summary>Renames a folder or module (audited).</summary>
    Task RenameContainerAsync(ObjectId containerId, string name, CancellationToken cancellationToken = default);
    /// <summary>Moves a folder or module under a new parent (null = top level) at a zero-based position among its new siblings.
    /// Rejects moves under itself or its own children, a folder inside a module, and a module move that would break the parent-to-child link rule.</summary>
    Task MoveContainerAsync(ObjectId containerId, ObjectId? newParentId, int index, CancellationToken cancellationToken = default);
    /// <summary>Deletes an EMPTY folder or module (no children, no objects including deleted ones, no baselines); its saved views and attribute columns go with it.</summary>
    Task DeleteContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default);
    Task SoftDeleteContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default);
    Task RestoreContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default);
    Task PurgeContainerAsync(ObjectId containerId, CancellationToken cancellationToken = default);
    /// <summary>Stops a module using an attribute column. Refused while any object of the module still has a value for it.</summary>
    Task RemoveAttributeFromModuleAsync(ObjectId moduleId, ObjectId attributeId, CancellationToken cancellationToken = default);
    Task AddObjectAsync(EngineeringObject value, CancellationToken cancellationToken = default);
    Task AddRelationshipAsync(TypedRelationship value, CancellationToken cancellationToken = default);
    /// <summary>Adds objects then relationships in one transaction: all are committed or none are.</summary>
    Task AddBatchAsync(IReadOnlyCollection<EngineeringObject> objects, IReadOnlyCollection<TypedRelationship> relationships, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Container>> ListContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Container>> ListAllContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EngineeringObject>> ListModuleObjectsAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EngineeringObject>> ListObjectsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TypedRelationship>> ListRelationshipsAsync(CancellationToken cancellationToken = default);
    Task<IntegrityReport> CheckIntegrityAsync(CancellationToken cancellationToken = default);

    // ----- Modules, outline, attributes (schema v3) -----
    Task AddAttributeDefinitionAsync(AttributeDefinition value, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttributeDefinition>> ListAttributeDefinitionsAsync(CancellationToken cancellationToken = default);
    /// <summary>Makes an attribute apply to a module (appended as the last column).</summary>
    Task AssignAttributeToModuleAsync(ObjectId moduleId, ObjectId attributeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttributeDefinition>> ListModuleAttributesAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Appends an object (which must belong to the module) as the last child of parentId, or at top level when null.</summary>
    Task AddToOutlineAsync(ObjectId moduleId, ObjectId objectId, ObjectId? parentId, bool isHeading, CancellationToken cancellationToken = default);
    Task MoveOutlineEntryAsync(ObjectId moduleId, ObjectId objectId, ObjectId? newParentId, int index, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutlineEntry>> GetOutlineAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Sets (or, when blank, clears) an attribute value. Validates type, module assignment and required-ness.</summary>
    Task SetAttributeValueAsync(ObjectId objectId, ObjectId attributeId, string? value, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttributeValue>> ListAttributeValuesAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Objects in the module lacking a value for a required attribute assigned to the module.</summary>
    Task<IReadOnlyList<MissingValue>> FindMissingRequiredValuesAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Creates a link from an object in a module to an object in one of that module's DIRECT child modules (parent level to child level).</summary>
    Task<TypedRelationship> AddLinkAsync(ObjectId sourceId, ObjectId targetId, RelationshipType type, CancellationToken cancellationToken = default);
    /// <summary>Removes a link (the removal is audited). Throws when it does not exist.</summary>
    Task RemoveLinkAsync(ObjectId relationshipId, CancellationToken cancellationToken = default);
    /// <summary>Links whose source or target changed since the link was created or last reviewed (schema v7). Null module = whole project.</summary>
    Task<IReadOnlyList<SuspectLink>> ListSuspectLinksAsync(ObjectId? moduleId, CancellationToken cancellationToken = default);
    /// <summary>Marks the links reviewed at the current revisions of both ends; audited.</summary>
    Task ClearSuspectAsync(IReadOnlyCollection<ObjectId> relationshipIds, CancellationToken cancellationToken = default);

    /// <summary>The newest audit entries first, at most <paramref name="limit"/> (1 to 5000). Read-only.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken cancellationToken = default);

    // ----- Comments and attachments (schema v8) -----
    /// <summary>Adds a comment; with a parent it is a reply to a top-level comment of the same object. Comments are append-only.</summary>
    Task<Comment> AddCommentAsync(ObjectId objectId, ObjectId? parentId, string body, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Comment>> ListCommentsAsync(ObjectId objectId, CancellationToken cancellationToken = default);
    /// <summary>Resolves or reopens a thread by its top-level comment id (recorded as an event; nothing is overwritten).</summary>
    Task SetThreadResolvedAsync(ObjectId threadId, bool resolved, CancellationToken cancellationToken = default);
    /// <summary>Open (unresolved) thread counts per object of the module.</summary>
    Task<IReadOnlyDictionary<ObjectId, int>> CountOpenThreadsAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Records that a managed attachment (already copied into the package) belongs to the object, or to one of its comments when commentId is given.</summary>
    Task<ObjectAttachment> AddAttachmentAsync(ObjectId objectId, string attachmentId, string fileName, long sizeBytes, string sha256, ObjectId? commentId = null, CancellationToken cancellationToken = default);
    /// <summary>All attachments of the object, including those attached to its comments (see CommentId).</summary>
    Task<IReadOnlyList<ObjectAttachment>> ListAttachmentsAsync(ObjectId objectId, CancellationToken cancellationToken = default);
    /// <summary>Hides the attachment record; the file stays in the package.</summary>
    Task RemoveAttachmentAsync(ObjectId attachmentRecordId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<ObjectId, int>> CountAttachmentsAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Ids of the managed files that any attachment record refers to, removed records included (their files are kept on purpose). Files in the package that are not listed here are unused.</summary>
    Task<IReadOnlySet<string>> ListReferencedAttachmentIdsAsync(CancellationToken cancellationToken = default);

    // ----- Saved module views (schema v4) -----
    /// <summary>Saves a view; an existing view of the same module with the same name (case-insensitive) is replaced.</summary>
    Task SaveViewAsync(SavedView view, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedView>> ListViewsAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    Task DeleteViewAsync(ObjectId viewId, CancellationToken cancellationToken = default);

    // ----- Object history and baselines (schema v5) -----
    /// <summary>Changes an object's text. Every change to text or attribute values adds an immutable revision.</summary>
    Task RenameObjectAsync(ObjectId objectId, string title, CancellationToken cancellationToken = default);
    /// <summary>Revisions of an object, newest first.</summary>
    Task<IReadOnlyList<ObjectRevision>> ListRevisionsAsync(ObjectId objectId, CancellationToken cancellationToken = default);
    /// <summary>Freezes the module's current objects, attribute values, outline numbers and links under a name (unique per module).</summary>
    Task<Baseline> CreateBaselineAsync(ObjectId moduleId, string name, string? note, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Baseline>> ListBaselinesAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    Task<BaselineSnapshot?> GetBaselineAsync(ObjectId baselineId, CancellationToken cancellationToken = default);
    /// <summary>The module's current content in the same shape as a baseline, for comparison.</summary>
    Task<ModuleSnapshot> CaptureModuleAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Freezes a module and every module below it in one step, all under the same baseline name.</summary>
    Task<IReadOnlyList<Baseline>> CreateBaselineSetAsync(ObjectId rootModuleId, string name, string? note, CancellationToken cancellationToken = default);

    // ----- Delete, restore, paste (schema v6) -----
    /// <summary>Canonical IDs of every object including deleted ones (IDs are never reused).</summary>
    Task<IReadOnlyList<string>> ListAllCanonicalIdsAsync(CancellationToken cancellationToken = default);
    /// <summary>Deletes an object and its whole outline section. Nothing is destroyed: the section can be restored. Returns the deleted section (batch, root, object count).</summary>
    Task<DeletedSection> DeleteSectionAsync(ObjectId moduleId, ObjectId rootObjectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeletedSection>> ListDeletedAsync(ObjectId moduleId, CancellationToken cancellationToken = default);
    /// <summary>Restores a deleted section under its original parent (which must not itself be deleted), at index among its siblings (null = last).</summary>
    Task<int> RestoreDeletedAsync(ObjectId batchId, int? index = null, CancellationToken cancellationToken = default);
    /// <summary>Adds new objects (a copied section) in one transaction, below parentId (null = top level), at index among its siblings (null = last).</summary>
    Task AddSectionAsync(ObjectId moduleId, ObjectId? parentId, int? index, IReadOnlyList<SectionItem> items, CancellationToken cancellationToken = default);

    // ----- Restore a revision, import (schema v6) -----
    /// <summary>Makes an object's text and attribute values equal to an earlier revision, as a NEW revision (history is never rewritten).
    /// Returns false when the object already matches. Attributes no longer assigned to the module are left alone.</summary>
    Task<bool> RestoreRevisionAsync(ObjectId objectId, long revision, CancellationToken cancellationToken = default);
    /// <summary>Applies a planned import in one transaction: new attributes, new objects (in order, parents first), updates, then links.</summary>
    Task<ImportOutcome> ImportAsync(ObjectId moduleId, IReadOnlyList<AttributeDefinition> newAttributes, IReadOnlyList<ObjectId> assignAttributes,
        IReadOnlyList<SectionItem> creates, IReadOnlyList<ObjectUpdate> updates, IReadOnlyList<(ObjectId Source, ObjectId Target, RelationshipType Type)> links,
        CancellationToken cancellationToken = default);
}

/// <summary>Findings make the package invalid; warnings (e.g. interrupted-write leftovers) need attention but do not block opening.
/// Every message starts with a category tag such as [manifest], [database], [relationship], [attachment], [recovery].</summary>
public sealed record IntegrityReport(bool IsValid, IReadOnlyList<string> Findings, IReadOnlyList<string>? Warnings = null);

public interface IProjectPackageService
{
    Task CreateAsync(string packagePath, string displayName, CancellationToken cancellationToken = default);
    /// <summary>Checks the package and opens its store, migrating the schema when needed. Refuses invalid packages.</summary>
    Task<IProjectStore> OpenAsync(string packagePath, CancellationToken cancellationToken = default);
    Task<IntegrityReport> CheckAsync(string packagePath, CancellationToken cancellationToken = default);
    Task CreateBackupAsync(string packagePath, string archivePath, CancellationToken cancellationToken = default);
    Task RestoreBackupAsync(string archivePath, string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>Local structured diagnostics. Implementations must never throw into project operations.</summary>
public interface IDiagnosticLog
{
    void Write(string level, string eventName, string message, string? correlationId = null);
}

public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static readonly NullDiagnosticLog Instance = new();
    public void Write(string level, string eventName, string message, string? correlationId = null) { }
}
