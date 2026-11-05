using System.Text.Json.Serialization;

namespace TraceForge.Domain;

public enum LinkFilter { Any, NoOutLinks, NoInLinks, MissingRequired }

/// <summary>A module table view: row filter plus which attribute columns are shown (null = all). Attribute ids are "N" guid text.</summary>
public sealed record ViewDefinition(string Text, string? AttributeId, string AttributeText, LinkFilter LinkFilter, IReadOnlyList<string>? ShownAttributeIds)
{
    public static ViewDefinition Default { get; } = new("", null, "", LinkFilter.Any, null);

    [JsonIgnore]
    public bool IsFiltering => Text.Trim().Length > 0 || (AttributeId is not null && AttributeText.Trim().Length > 0) || LinkFilter != LinkFilter.Any;
}

public sealed record SavedView(ObjectId Id, ObjectId ModuleId, string Name, ViewDefinition Definition, DateTimeOffset CreatedUtc);

/// <summary>What a filter needs to know about one table row.</summary>
public sealed record RowFacts(string Number, string CanonicalId, string Title, IReadOnlyDictionary<string, string> Values, int InLinks, int OutLinks, bool MissingRequired);

public static class ViewFilter
{
    public static bool Matches(ViewDefinition view, RowFacts row)
    {
        var text = view.Text.Trim();
        if (text.Length > 0 && !(Has(row.Number, text) || Has(row.CanonicalId, text) || Has(row.Title, text) || row.Values.Values.Any(v => Has(v, text)))) return false;
        var attributeText = view.AttributeText.Trim();
        if (view.AttributeId is { } attributeId && attributeText.Length > 0 && !Has(row.Values.GetValueOrDefault(attributeId, ""), attributeText)) return false;
        return view.LinkFilter switch
        {
            LinkFilter.NoOutLinks => row.OutLinks == 0,
            LinkFilter.NoInLinks => row.InLinks == 0,
            LinkFilter.MissingRequired => row.MissingRequired,
            _ => true,
        };
    }

    private static bool Has(string value, string needle) => value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
