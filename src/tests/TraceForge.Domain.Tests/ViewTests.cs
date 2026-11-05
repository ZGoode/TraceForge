using System.Text.Json;
using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class ViewTests
{
    private static RowFacts Row(string number = "1.1", string id = "SRS-001", string title = "Boot time", int inLinks = 0, int outLinks = 0, bool missing = false, params (string, string)[] values) =>
        new(number, id, title, values.ToDictionary(v => v.Item1, v => v.Item2), inLinks, outLinks, missing);
    private static ViewDefinition V(string text = "", string? attr = null, string attrText = "", LinkFilter link = LinkFilter.Any) => new(text, attr, attrText, link, null);

    [Fact] public void Default_view_matches_everything_and_is_not_filtering()
    { Assert.True(ViewFilter.Matches(ViewDefinition.Default, Row())); Assert.False(ViewDefinition.Default.IsFiltering); }

    [Theory] [InlineData("boot", true)] [InlineData("SRS-001", true)] [InlineData("1.1", true)] [InlineData("approved", true)] [InlineData("nothing", false)]
    public void Text_searches_number_id_title_and_attribute_values(string text, bool expected) =>
        Assert.Equal(expected, ViewFilter.Matches(V(text), Row(values: ("a1", "Approved"))));

    [Fact] public void Attribute_filter_applies_only_to_the_chosen_attribute_and_only_with_text()
    {
        var row = Row(values: [("a1", "Draft"), ("a2", "Approved")]);
        Assert.False(ViewFilter.Matches(V(attr: "a1", attrText: "approved"), row));
        Assert.True(ViewFilter.Matches(V(attr: "a2", attrText: "approved"), row));
        Assert.True(ViewFilter.Matches(V(attr: "a1", attrText: ""), row)); // no text -> no attribute filter
        Assert.False(ViewFilter.Matches(V(attr: "missing", attrText: "x"), row));
    }

    [Fact] public void Link_filters()
    {
        Assert.True(ViewFilter.Matches(V(link: LinkFilter.NoOutLinks), Row(outLinks: 0))); Assert.False(ViewFilter.Matches(V(link: LinkFilter.NoOutLinks), Row(outLinks: 2)));
        Assert.True(ViewFilter.Matches(V(link: LinkFilter.NoInLinks), Row(inLinks: 0)));   Assert.False(ViewFilter.Matches(V(link: LinkFilter.NoInLinks), Row(inLinks: 1)));
        Assert.True(ViewFilter.Matches(V(link: LinkFilter.MissingRequired), Row(missing: true))); Assert.False(ViewFilter.Matches(V(link: LinkFilter.MissingRequired), Row()));
    }

    [Fact] public void View_definition_round_trips_through_json()
    {
        var v = new ViewDefinition("x", "a1", "y", LinkFilter.NoInLinks, ["a1", "a2"]);
        var back = JsonSerializer.Deserialize<ViewDefinition>(JsonSerializer.Serialize(v))!;
        Assert.Equal(v.Text, back.Text); Assert.Equal(v.AttributeId, back.AttributeId); Assert.Equal(v.AttributeText, back.AttributeText);
        Assert.Equal(v.LinkFilter, back.LinkFilter); Assert.Equal(new[] { "a1", "a2" }, back.ShownAttributeIds);
        Assert.True(back.IsFiltering);
    }
}
