using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class ImpactTests
{
    private static TypedRelationship L(ObjectId a, ObjectId b) => TypedRelationship.Create(a, b, RelationshipType.Decomposes);

    [Fact] public void Downstream_reports_each_object_once_at_its_shallowest_depth_and_survives_cycles()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), c = ObjectId.New(), d = ObjectId.New();
        var links = new[] { L(a, b), L(b, c), L(a, c), L(c, d), L(d, a) };
        var result = Impact.Analyze(a, links, downstream: true);
        Assert.Equal(3, result.Count);
        Assert.Equal(new HashSet<ObjectId> { b, c }, result.Where(r => r.Depth == 1).Select(r => r.ObjectId).ToHashSet());
        Assert.Equal(d, Assert.Single(result, r => r.Depth == 2).ObjectId);
        Assert.DoesNotContain(result, r => r.ObjectId == a);
    }

    [Fact] public void Upstream_follows_links_backwards()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), c = ObjectId.New();
        var result = Impact.Analyze(c, [L(a, b), L(b, c)], downstream: false);
        Assert.Equal(new ObjectId[] { b, a }, result.Select(r => r.ObjectId).ToArray());
        Assert.Empty(Impact.Analyze(a, [L(a, b)], downstream: false));
    }
}
