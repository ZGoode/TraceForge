namespace TraceForge.Domain;

public enum EngineeringObjectType { Requirement, DesignElement, ImplementationTask, VerificationCase, VerificationRun, Module, Goal, Assumption, Constraint, Decision, Issue }
public enum RelationshipType { Decomposes, DerivedFrom, Satisfies, Implements, Verifies, DependsOn, Constrains, Supersedes, References }
public enum ContainerKind { Module, Folder }

public readonly record struct ObjectId(Guid Value)
{
    public static ObjectId New() => new(Guid.NewGuid());
    public static ObjectId Parse(string value) => new(Guid.ParseExact(value, "N"));
    public override string ToString() => Value.ToString("N");
}

/// <summary>Organizational container (module or folder). Containers form a tree; a parent must exist before its children.</summary>
public sealed record Container(ObjectId Id, ObjectId? ParentId, ContainerKind Kind, string Name, int SortOrder, DateTimeOffset CreatedUtc, DateTimeOffset? DeletedUtc = null)
{
    /// <summary>True for a folder or module that was deleted (and can still be restored or purged).</summary>
    public bool IsDeleted => DeletedUtc is not null;

    public static Container Create(ContainerKind kind, string name, ObjectId? parentId = null, int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Container name is required.", nameof(name));
        return new(ObjectId.New(), parentId, kind, name.Trim(), sortOrder, DateTimeOffset.UtcNow);
    }
}

public sealed record EngineeringObject(ObjectId Id, EngineeringObjectType Type, string CanonicalId, string Title, long Revision, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, ObjectId? ContainerId = null)
{
    public static EngineeringObject Create(EngineeringObjectType type, string canonicalId, string title, ObjectId? containerId = null)
    {
        if (string.IsNullOrWhiteSpace(canonicalId)) throw new ArgumentException("Canonical ID is required.", nameof(canonicalId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title is required.", nameof(title));
        var now = DateTimeOffset.UtcNow;
        return new(ObjectId.New(), type, canonicalId.Trim(), title.Trim(), 1, now, now, containerId);
    }
}

public sealed record TypedRelationship(ObjectId Id, ObjectId SourceId, ObjectId TargetId, RelationshipType Type, DateTimeOffset CreatedUtc)
{
    public static TypedRelationship Create(ObjectId sourceId, ObjectId targetId, RelationshipType type)
    {
        if (sourceId == targetId && type == RelationshipType.Decomposes) throw new ArgumentException("An object cannot decompose itself.");
        return new(ObjectId.New(), sourceId, targetId, type, DateTimeOffset.UtcNow);
    }
}
