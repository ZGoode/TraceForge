using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class SnapshotTests
{
    private static BaselineObject Obj(ObjectId id, string number, string cid, string title, params (string, string)[] values) =>
        new(id, number, cid, title, false, values.ToDictionary(v => v.Item1, v => v.Item2));
    private static ModuleSnapshot Snap(IEnumerable<BaselineObject> objects, params BaselineLink[] links) => new(objects.ToList(), links);
    private static IReadOnlyList<ObjectChange> Diff(ModuleSnapshot a, ModuleSnapshot b) => Snapshots.Compare(a, b, k => "attr-" + k, id => id.ToString()[..4]);

    [Fact] public void Identical_snapshots_have_no_changes()
    {
        var a = ObjectId.New();
        var s = Snap([Obj(a, "1", "R-1", "Boot", ("p", "high"))]);
        Assert.Empty(Diff(s, s));
    }

    [Fact] public void Added_and_removed_objects_are_reported()
    {
        var a = ObjectId.New(); var b = ObjectId.New();
        var changes = Diff(Snap([Obj(a, "1", "R-1", "Boot")]), Snap([Obj(b, "1", "R-2", "Logging")]));
        Assert.Equal(new[] { ChangeKind.Added, ChangeKind.Removed }, changes.Select(c => c.Kind));
        Assert.Equal("R-2", changes[0].CanonicalId); Assert.Equal("R-1", changes[1].CanonicalId);
    }

    [Fact] public void Text_and_value_changes_are_described_with_attribute_names()
    {
        var a = ObjectId.New();
        var change = Assert.Single(Diff(Snap([Obj(a, "1", "R-1", "Boot", ("p", "low"))]), Snap([Obj(a, "1", "R-1", "Boot fast", ("p", "high"), ("s", "open"))])));
        Assert.Equal(ChangeKind.Changed, change.Kind);
        Assert.Contains("Text: 'Boot' → 'Boot fast'", change.Description);
        Assert.Contains("attr-p: 'low' → 'high'", change.Description);
        Assert.Contains("attr-s: '(none)' → 'open'", change.Description);
    }

    [Fact] public void Renumbering_is_reported_as_moved()
    {
        var a = ObjectId.New();
        var change = Assert.Single(Diff(Snap([Obj(a, "1.2", "R-1", "Boot")]), Snap([Obj(a, "1.3", "R-1", "Boot")])));
        Assert.Equal(ChangeKind.Moved, change.Kind); Assert.Contains("1.2 → 1.3", change.Description);
    }

    [Fact] public void Link_additions_and_removals_are_reported()
    {
        var a = ObjectId.New(); var b = ObjectId.New(); var c = ObjectId.New();
        var objects = new[] { Obj(a, "1", "R-1", "Boot") };
        var kept = new BaselineLink(a, b, RelationshipType.Decomposes);
        var old = new BaselineLink(a, c, RelationshipType.Verifies);
        var added = new BaselineLink(a, c, RelationshipType.Satisfies);
        var changes = Diff(Snap(objects, kept, old), Snap(objects, kept, added));
        Assert.Equal(new[] { ChangeKind.LinkAdded, ChangeKind.LinkRemoved }, changes.Select(x => x.Kind));
        Assert.Contains("Satisfies", changes[0].Description); Assert.Contains("Verifies", changes[1].Description);
    }
}
