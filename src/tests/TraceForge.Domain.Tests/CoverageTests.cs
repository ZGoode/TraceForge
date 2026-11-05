using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class CoverageTests
{
    [Fact] public void Reports_untraced_objects_only_in_the_directions_the_module_level_expects()
    {
        var top = new CoverageModule(ObjectId.New(), "SRS", false, true);
        var mid = new CoverageModule(ObjectId.New(), "SDD", true, true);
        var low = new CoverageModule(ObjectId.New(), "Tests", true, false);
        var a = new CoverageObject(ObjectId.New(), top.Id, "1", "SRS-1", "A");
        var b = new CoverageObject(ObjectId.New(), top.Id, "2", "SRS-2", "B");
        var c = new CoverageObject(ObjectId.New(), mid.Id, "1", "SDD-1", "C");
        var d = new CoverageObject(ObjectId.New(), low.Id, "1", "T-1", "D");
        var e = new CoverageObject(ObjectId.New(), low.Id, "2", "T-2", "E");
        var links = new[] { TypedRelationship.Create(a.Id, c.Id, RelationshipType.Decomposes), TypedRelationship.Create(c.Id, d.Id, RelationshipType.Verifies) };

        var report = Coverage.Compute([top, mid, low], [a, b, c, d, e], links);

        var srs = report.Modules[0];
        Assert.Equal((2, 1, 0), (srs.Total, srs.WithDownstream, srs.WithUpstream));
        Assert.Equal(50, srs.DownstreamPercent);
        Assert.Equal(100, report.Modules[1].DownstreamPercent);
        Assert.Equal(2, report.Gaps.Count);
        Assert.Contains(report.Gaps, g => g.Kind == GapKind.NoDownstream && g.CanonicalId == "SRS-2");     // top level: no upstream expected, so A/B never appear as NoUpstream
        Assert.Contains(report.Gaps, g => g.Kind == GapKind.NoUpstream && g.CanonicalId == "T-2");        // bottom level: no downstream expected
    }

    [Fact] public void An_empty_module_is_fully_covered() =>
        Assert.Equal(100, Coverage.Compute([new CoverageModule(ObjectId.New(), "Empty", true, true)], [], []).Modules[0].DownstreamPercent);
}
