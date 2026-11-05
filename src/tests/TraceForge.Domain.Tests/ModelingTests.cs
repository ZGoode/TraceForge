using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class ModelingTests
{
    private static readonly ObjectId M = ObjectId.New();
    private static OutlineEntry E(ObjectId id, ObjectId? parent, int order, bool heading = false) => new(id, M, parent, order, heading);

    [Fact] public void Outline_numbers_follow_hierarchy_and_order()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), a1 = ObjectId.New(), a2 = ObjectId.New(), a2x = ObjectId.New();
        var n = Outline.Number([E(b, null, 1), E(a, null, 0, true), E(a2, a, 1), E(a1, a, 0), E(a2x, a2, 0)]);
        Assert.Equal("1", n[a]); Assert.Equal("2", n[b]); Assert.Equal("1.1", n[a1]); Assert.Equal("1.2", n[a2]); Assert.Equal("1.2.1", n[a2x]);
    }

    [Fact] public void Move_reorders_siblings_and_compacts_the_old_parent()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), c = ObjectId.New(), c1 = ObjectId.New();
        var moved = Outline.Move([E(a, null, 0), E(b, null, 1), E(c, null, 2), E(c1, c, 0)], a, c, 0); // a becomes first child of c
        var n = Outline.Number(moved);
        Assert.Equal("1", n[b]); Assert.Equal("2", n[c]); Assert.Equal("2.1", n[a]); Assert.Equal("2.2", n[c1]);
        Assert.Empty(Outline.Validate(moved));
    }

    [Fact] public void Move_within_same_parent_to_the_end_clamps_the_index()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), c = ObjectId.New();
        var n = Outline.Number(Outline.Move([E(a, null, 0), E(b, null, 1), E(c, null, 2)], a, null, 99));
        Assert.Equal("1", n[b]); Assert.Equal("2", n[c]); Assert.Equal("3", n[a]);
    }

    [Fact] public void Move_rejects_self_and_descendant_targets_and_unknown_ids()
    {
        ObjectId a = ObjectId.New(), a1 = ObjectId.New(), a11 = ObjectId.New();
        var entries = new[] { E(a, null, 0), E(a1, a, 0), E(a11, a1, 0) };
        Assert.Throws<InvalidOperationException>(() => Outline.Move(entries, a, a, 0));
        Assert.Throws<InvalidOperationException>(() => Outline.Move(entries, a, a11, 0));
        Assert.Throws<ArgumentException>(() => Outline.Move(entries, ObjectId.New(), null, 0));
        Assert.Throws<ArgumentException>(() => Outline.Move(entries, a, ObjectId.New(), 0));
    }

    [Fact] public void Validate_reports_cycles_missing_parents_and_duplicate_positions()
    {
        ObjectId a = ObjectId.New(), b = ObjectId.New(), c = ObjectId.New(), d = ObjectId.New();
        Assert.Contains(Outline.Validate([E(a, b, 0), E(b, a, 0)]), p => p.Contains("cycle"));
        Assert.Contains(Outline.Validate([E(a, ObjectId.New(), 0)]), p => p.Contains("missing"));
        Assert.Contains(Outline.Validate([E(c, null, 0), E(d, null, 0)]), p => p.Contains("share position"));
    }

    [Theory]
    [InlineData(AttributeDataType.Integer, " 42 ", "42")] [InlineData(AttributeDataType.Real, "1.50", "1.5")]
    [InlineData(AttributeDataType.Boolean, "TRUE", "true")] [InlineData(AttributeDataType.Date, "2026-10-18", "2026-10-18")]
    public void Valid_values_are_normalized_to_canonical_text(AttributeDataType type, string raw, string expected)
    {
        var d = AttributeDefinition.Create("A", type);
        Assert.True(d.TryNormalize(raw, out var normalized, out var error)); Assert.Null(error); Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(AttributeDataType.Integer, "4.5")] [InlineData(AttributeDataType.Real, "abc")] [InlineData(AttributeDataType.Boolean, "yes")]
    [InlineData(AttributeDataType.Date, "10/18/2026")] [InlineData(AttributeDataType.Date, "2026-02-30")]
    public void Invalid_values_are_rejected_with_a_message(AttributeDataType type, string raw)
    {
        var d = AttributeDefinition.Create("A", type);
        Assert.False(d.TryNormalize(raw, out _, out var error)); Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact] public void Enumeration_matches_case_insensitively_and_returns_the_canonical_spelling()
    {
        var d = AttributeDefinition.Create("Status", AttributeDataType.Enumeration, enumValues: ["Draft", "Approved"]);
        Assert.True(d.TryNormalize("approved", out var n, out _)); Assert.Equal("Approved", n);
        Assert.False(d.TryNormalize("Done", out _, out var error)); Assert.Contains("Draft", error);
    }

    [Fact] public void Definition_rules()
    {
        Assert.Throws<ArgumentException>(() => AttributeDefinition.Create(" ", AttributeDataType.Text));
        Assert.Throws<ArgumentException>(() => AttributeDefinition.Create("E", AttributeDataType.Enumeration));
        Assert.Throws<ArgumentException>(() => AttributeDefinition.Create("E", AttributeDataType.Enumeration, enumValues: ["a", "A"]));
        Assert.Throws<ArgumentException>(() => AttributeDefinition.Create("T", AttributeDataType.Text, enumValues: ["a"]));
    }
}
