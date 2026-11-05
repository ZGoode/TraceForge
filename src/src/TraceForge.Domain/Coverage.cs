namespace TraceForge.Domain;

/// <summary>A module taking part in a coverage report. HasParent/HasChild say whether upstream/downstream links are expected for its objects.</summary>
public sealed record CoverageModule(ObjectId Id, string Name, bool HasParent, bool HasChild);

/// <summary>An object (not a heading) of a module, in outline order.</summary>
public sealed record CoverageObject(ObjectId Id, ObjectId ModuleId, string Number, string CanonicalId, string Title);

/// <summary>Per-module totals. Percentages are of Total; a module with no objects reports 100%.</summary>
public sealed record ModuleCoverage(ObjectId ModuleId, string Name, int Total, int WithDownstream, int WithUpstream, bool ExpectsDownstream, bool ExpectsUpstream)
{
    public double DownstreamPercent => Total == 0 ? 100 : 100.0 * WithDownstream / Total;
    public double UpstreamPercent => Total == 0 ? 100 : 100.0 * WithUpstream / Total;
}

public enum GapKind { NoDownstream, NoUpstream }

public sealed record CoverageGap(GapKind Kind, ObjectId ModuleId, string ModuleName, string Number, string CanonicalId, string Title)
{
    public string Description => Kind == GapKind.NoDownstream ? "No link to a child-level object" : "Not linked from any parent-level object";
}

public sealed record CoverageReport(IReadOnlyList<ModuleCoverage> Modules, IReadOnlyList<CoverageGap> Gaps);

public static class Coverage
{
    /// <summary>Counts objects that have a downstream link (module has a child level) or an upstream link (module has a parent level)
    /// and lists the ones that do not. Links to objects outside <paramref name="objects"/> still count. Gaps keep the order of the inputs.</summary>
    public static CoverageReport Compute(IReadOnlyList<CoverageModule> modules, IReadOnlyList<CoverageObject> objects, IEnumerable<TypedRelationship> links)
    {
        var linkList = links.ToList();
        var hasOut = linkList.Select(l => l.SourceId).ToHashSet();
        var hasIn = linkList.Select(l => l.TargetId).ToHashSet();
        var summaries = new List<ModuleCoverage>();
        var gaps = new List<CoverageGap>();
        foreach (var m in modules)
        {
            var mine = objects.Where(o => o.ModuleId == m.Id).ToList();
            summaries.Add(new ModuleCoverage(m.Id, m.Name, mine.Count, mine.Count(o => hasOut.Contains(o.Id)), mine.Count(o => hasIn.Contains(o.Id)), m.HasChild, m.HasParent));
            foreach (var o in mine)
            {
                if (m.HasChild && !hasOut.Contains(o.Id)) gaps.Add(new CoverageGap(GapKind.NoDownstream, m.Id, m.Name, o.Number, o.CanonicalId, o.Title));
                if (m.HasParent && !hasIn.Contains(o.Id)) gaps.Add(new CoverageGap(GapKind.NoUpstream, m.Id, m.Name, o.Number, o.CanonicalId, o.Title));
            }
        }
        return new CoverageReport(summaries, gaps);
    }
}
