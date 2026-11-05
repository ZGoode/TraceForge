namespace TraceForge.Domain;

/// <summary>One immutable revision of an object: its text and attribute values (attribute id "N" text to canonical value) after a change.</summary>
public sealed record ObjectRevision(ObjectId ObjectId, long Revision, string Title, IReadOnlyDictionary<string, string> Values, DateTimeOffset ChangedUtc, string Actor, string Summary);

/// <summary>A named, frozen snapshot of one module. Baselines can never be edited or deleted.</summary>
public sealed record Baseline(ObjectId Id, ObjectId ModuleId, string Name, string Note, DateTimeOffset CreatedUtc);

public sealed record BaselineObject(ObjectId ObjectId, string Number, string CanonicalId, string Title, bool IsHeading, IReadOnlyDictionary<string, string> Values);

public sealed record BaselineLink(ObjectId SourceId, ObjectId TargetId, RelationshipType Type);

/// <summary>The content of a module at one moment: its objects (with outline numbers) and every link touching it.</summary>
public sealed record ModuleSnapshot(IReadOnlyList<BaselineObject> Objects, IReadOnlyList<BaselineLink> Links);

public sealed record BaselineSnapshot(Baseline Baseline, ModuleSnapshot Content);

public enum ChangeKind { Added, Removed, Changed, Moved, LinkAdded, LinkRemoved }

public sealed record ObjectChange(ChangeKind Kind, ObjectId? ObjectId, string CanonicalId, string Description);

public static class Snapshots
{
    private const string None = "(none)";

    /// <summary>Differences going from <paramref name="from"/> to <paramref name="to"/>: added/removed objects, text and value changes,
    /// renumbering, and added/removed links.</summary>
    public static IReadOnlyList<ObjectChange> Compare(ModuleSnapshot from, ModuleSnapshot to, Func<string, string> attributeName, Func<ObjectId, string> objectLabel)
    {
        var before = from.Objects.ToDictionary(o => o.ObjectId);
        var after = to.Objects.ToDictionary(o => o.ObjectId);
        var changes = new List<ObjectChange>();

        foreach (var o in to.Objects.Where(o => !before.ContainsKey(o.ObjectId)))
            changes.Add(new(ChangeKind.Added, o.ObjectId, o.CanonicalId, $"{o.Number} {o.Title}".Trim()));
        foreach (var o in from.Objects.Where(o => !after.ContainsKey(o.ObjectId)))
            changes.Add(new(ChangeKind.Removed, o.ObjectId, o.CanonicalId, $"{o.Number} {o.Title}".Trim()));

        foreach (var o in to.Objects.Where(o => before.ContainsKey(o.ObjectId)))
        {
            var old = before[o.ObjectId];
            var parts = new List<string>();
            if (old.Title != o.Title) parts.Add($"Text: '{old.Title}' → '{o.Title}'");
            foreach (var key in old.Values.Keys.Union(o.Values.Keys).OrderBy(k => attributeName(k), StringComparer.OrdinalIgnoreCase))
            {
                var b = old.Values.GetValueOrDefault(key, None);
                var a = o.Values.GetValueOrDefault(key, None);
                if (b != a) parts.Add($"{attributeName(key)}: '{b}' → '{a}'");
            }
            if (parts.Count > 0) changes.Add(new(ChangeKind.Changed, o.ObjectId, o.CanonicalId, string.Join("; ", parts)));
            if (old.Number != o.Number && old.Number.Length > 0 && o.Number.Length > 0)
                changes.Add(new(ChangeKind.Moved, o.ObjectId, o.CanonicalId, $"Number {old.Number} → {o.Number}"));
        }

        var oldLinks = from.Links.ToHashSet();
        var newLinks = to.Links.ToHashSet();
        foreach (var l in newLinks.Where(l => !oldLinks.Contains(l)))
            changes.Add(new(ChangeKind.LinkAdded, l.SourceId, objectLabel(l.SourceId), $"{l.Type}: {objectLabel(l.SourceId)} → {objectLabel(l.TargetId)}"));
        foreach (var l in oldLinks.Where(l => !newLinks.Contains(l)))
            changes.Add(new(ChangeKind.LinkRemoved, l.SourceId, objectLabel(l.SourceId), $"{l.Type}: {objectLabel(l.SourceId)} → {objectLabel(l.TargetId)}"));

        return changes.OrderBy(c => (int)c.Kind).ThenBy(c => c.CanonicalId, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>One line of the project's append-only audit log.</summary>
public sealed record AuditEntry(long Seq, DateTimeOffset Utc, string Actor, string Action, string EntityType, string EntityId, string Detail);

