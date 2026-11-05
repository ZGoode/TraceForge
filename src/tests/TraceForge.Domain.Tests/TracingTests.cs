using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class TracingTests
{
    private static IEnumerable<ObjectId> None(ObjectId _) => [];
    private static IEnumerable<(ObjectId, string)> NoLinks(ObjectId _) => [];

    [Fact] public void Tree_contains_section_children_and_links_across_levels()
    {
        ObjectId need = ObjectId.New(), needChild = ObjectId.New(), sys = ObjectId.New(), sub = ObjectId.New();
        var sections = new Dictionary<ObjectId, ObjectId[]> { [need] = [needChild] };
        var links = new Dictionary<ObjectId, (ObjectId, string)[]> { [need] = [(sys, "Decomposes")], [needChild] = [], [sys] = [(sub, "Decomposes")] };
        var tree = Tracing.Build(need, "root", id => sections.GetValueOrDefault(id) ?? [], id => links.GetValueOrDefault(id) ?? []);
        Assert.Equal(2, tree.Children.Count);
        Assert.Equal("section", tree.Children[0].Kind); Assert.Equal(needChild, tree.Children[0].ObjectId);
        Assert.Equal("Decomposes", tree.Children[1].Kind); Assert.Equal(sys, tree.Children[1].ObjectId);
        Assert.Equal(sub, Assert.Single(tree.Children[1].Children).ObjectId);
    }

    [Fact] public void Cycles_are_cut_and_marked()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New();
        var tree = Tracing.Build(a, "root", None, id => id == a ? [(b, "L")] : [(a, "L")]);
        var leaf = tree.Children.Single().Children.Single();
        Assert.Equal(a, leaf.ObjectId); Assert.Contains("cycle", leaf.Kind); Assert.Empty(leaf.Children);
    }

    [Fact] public void Depth_limit_stops_runaway_chains()
    {
        var tree = Tracing.Build(ObjectId.New(), "root", None, _ => [(ObjectId.New(), "L")], maxDepth: 3);
        var depth = 0; for (var n = tree; n.Children.Count > 0; n = n.Children[0]) depth++;
        Assert.True(depth <= 3);
    }

    [Fact] public void A_leaf_has_no_children() => Assert.Empty(Tracing.Build(ObjectId.New(), "root", None, NoLinks).Children);
}
