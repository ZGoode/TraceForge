namespace TraceForge.Domain;

/// <summary>One node of a downstream trace tree. Kind is "root", "section" (outline child in the same module) or a link label.</summary>
public sealed record TraceNode(ObjectId ObjectId, string Kind, IReadOnlyList<TraceNode> Children);

public static class Tracing
{
    /// <summary>Builds the downstream tree under <paramref name="root"/>: its outline children (a section) and its link targets,
    /// recursively across module levels. A path that revisits an object, or exceeds maxDepth, is cut and marked.</summary>
    public static TraceNode Build(ObjectId root, string kind,
        Func<ObjectId, IEnumerable<ObjectId>> sectionChildren,
        Func<ObjectId, IEnumerable<(ObjectId Target, string Label)>> linkChildren,
        int maxDepth = 32)
    {
        TraceNode Visit(ObjectId id, string k, HashSet<ObjectId> path, int depth)
        {
            if (depth >= maxDepth || !path.Add(id)) return new TraceNode(id, k + " (cycle or depth limit)", []);
            var children = new List<TraceNode>();
            foreach (var child in sectionChildren(id)) children.Add(Visit(child, "section", path, depth + 1));
            foreach (var (target, label) in linkChildren(id)) children.Add(Visit(target, label, path, depth + 1));
            path.Remove(id);
            return new TraceNode(id, k, children);
        }
        return Visit(root, kind, new HashSet<ObjectId>(), 0);
    }
}

/// <summary>A link that needs review because one or both ends changed after the link was created or last reviewed.</summary>
public sealed record SuspectLink(ObjectId RelationshipId, ObjectId SourceId, ObjectId TargetId, RelationshipType Type, bool SourceChanged, bool TargetChanged);

/// <summary>One object reached by an impact analysis. Depth 1 is directly linked.</summary>
public sealed record ImpactItem(ObjectId ObjectId, int Depth, RelationshipType ViaType, ObjectId ViaObject);

public static class Impact
{
    /// <summary>Everything reachable from <paramref name="root"/> over links, breadth first. Downstream follows links source→target
    /// (what may need rework when root changes); upstream follows target→source (what root derives from). Each object is reported once, at its shallowest depth.</summary>
    public static IReadOnlyList<ImpactItem> Analyze(ObjectId root, IEnumerable<TypedRelationship> links, bool downstream, int maxDepth = 32)
    {
        var next = links.ToLookup(l => downstream ? l.SourceId : l.TargetId);
        var seen = new HashSet<ObjectId> { root };
        var result = new List<ImpactItem>();
        var frontier = new List<ObjectId> { root };
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var following = new List<ObjectId>();
            foreach (var from in frontier)
                foreach (var l in next[from].OrderBy(x => x.Id.ToString(), StringComparer.Ordinal))
                {
                    var to = downstream ? l.TargetId : l.SourceId;
                    if (!seen.Add(to)) continue;
                    result.Add(new ImpactItem(to, depth, l.Type, from));
                    following.Add(to);
                }
            frontier = following;
        }
        return result;
    }
}
