using System.Globalization;

namespace TraceForge.Domain;

public enum AttributeDataType { Text, Integer, Real, Boolean, Date, Enumeration }

/// <summary>Project-wide attribute definition. Modules choose which definitions apply to them.
/// Note: record equality compares EnumValues by reference; compare its contents explicitly.</summary>
public sealed record AttributeDefinition(ObjectId Id, string Name, AttributeDataType DataType, bool IsRequired, IReadOnlyList<string> EnumValues, DateTimeOffset CreatedUtc)
{
    public static AttributeDefinition Create(string name, AttributeDataType dataType, bool isRequired = false, IEnumerable<string>? enumValues = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Attribute name is required.", nameof(name));
        var values = (enumValues ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (dataType == AttributeDataType.Enumeration)
        {
            if (values.Count == 0) throw new ArgumentException("An enumeration attribute needs at least one value.", nameof(enumValues));
            if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count) throw new ArgumentException("Enumeration values must be unique (case-insensitive).", nameof(enumValues));
        }
        else if (values.Count > 0) throw new ArgumentException("Only enumeration attributes may define values.", nameof(enumValues));
        return new(ObjectId.New(), name.Trim(), dataType, isRequired, values, DateTimeOffset.UtcNow);
    }

    /// <summary>Validates a non-blank raw value and produces its canonical stored text (invariant culture, ISO dates, lowercase booleans).</summary>
    public bool TryNormalize(string raw, out string normalized, out string? error)
    {
        normalized = ""; error = null;
        var text = raw.Trim();
        switch (DataType)
        {
            case AttributeDataType.Text: normalized = raw; return true;
            case AttributeDataType.Integer:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { normalized = i.ToString(CultureInfo.InvariantCulture); return true; }
                error = $"'{raw}' is not a whole number."; return false;
            case AttributeDataType.Real:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) { normalized = d.ToString("R", CultureInfo.InvariantCulture); return true; }
                error = $"'{raw}' is not a number."; return false;
            case AttributeDataType.Boolean:
                if (bool.TryParse(text, out var b)) { normalized = b ? "true" : "false"; return true; }
                error = $"'{raw}' is not true or false."; return false;
            case AttributeDataType.Date:
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); return true; }
                error = $"'{raw}' is not a date in yyyy-MM-dd form."; return false;
            case AttributeDataType.Enumeration:
                var match = EnumValues.FirstOrDefault(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));
                if (match is not null) { normalized = match; return true; }
                error = $"'{raw}' is not one of: {string.Join(", ", EnumValues)}."; return false;
            default: error = "Unknown attribute type."; return false;
        }
    }
}

public sealed record AttributeValue(ObjectId ObjectId, ObjectId AttributeId, string Value);
public sealed record MissingValue(ObjectId ObjectId, ObjectId AttributeId);

/// <summary>Position of an object inside a module's outline. ParentId null means top level. Outline numbers are derived, never stored.</summary>
public sealed record OutlineEntry(ObjectId ObjectId, ObjectId ModuleId, ObjectId? ParentId, int SortOrder, bool IsHeading);

/// <summary>Pure outline rules for ONE module's entries.</summary>
public static class Outline
{
    private static IEnumerable<OutlineEntry> Ordered(IEnumerable<OutlineEntry> entries) =>
        entries.OrderBy(e => e.SortOrder).ThenBy(e => e.ObjectId.ToString(), StringComparer.Ordinal);

    /// <summary>Outline numbers such as "1", "1.1", "1.2.3", derived from parent/order. Unreachable (cyclic) entries get no number.</summary>
    public static IReadOnlyDictionary<ObjectId, string> Number(IReadOnlyCollection<OutlineEntry> entries)
    {
        var children = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        var result = new Dictionary<ObjectId, string>();
        void Walk(IEnumerable<OutlineEntry> siblings, string prefix)
        {
            var n = 1;
            foreach (var entry in Ordered(siblings))
            {
                var number = prefix.Length == 0 ? n.ToString(CultureInfo.InvariantCulture) : $"{prefix}.{n.ToString(CultureInfo.InvariantCulture)}";
                result[entry.ObjectId] = number;
                Walk(children[entry.ObjectId], number);
                n++;
            }
        }
        Walk(entries.Where(e => e.ParentId is null), "");
        return result;
    }

    /// <summary>Problems in a stored outline: missing parent, cycles, duplicate positions.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyCollection<OutlineEntry> entries)
    {
        var problems = new List<string>();
        var byId = entries.ToDictionary(e => e.ObjectId);
        foreach (var e in entries)
            if (e.ParentId is { } p && !byId.ContainsKey(p)) problems.Add($"Entry {e.ObjectId} has a parent that is missing from its module: {p}");
        foreach (var e in entries)
        {
            var seen = new HashSet<ObjectId> { e.ObjectId };
            var current = e;
            while (current.ParentId is { } p && byId.TryGetValue(p, out var parent))
            {
                if (!seen.Add(p)) { problems.Add($"Entry {e.ObjectId} is part of an outline cycle."); break; }
                current = parent;
            }
        }
        foreach (var group in entries.GroupBy(e => e.ParentId))
            foreach (var dup in group.GroupBy(e => e.SortOrder).Where(g => g.Count() > 1))
                problems.Add($"{dup.Count()} entries share position {dup.Key} under the same parent.");
        return problems;
    }

    /// <summary>Moves an entry under a new parent (null = top level) at a zero-based index among its new siblings,
    /// compacting sibling orders. Rejects moves under itself or its own descendants. Returns the full updated list.</summary>
    public static IReadOnlyList<OutlineEntry> Move(IReadOnlyCollection<OutlineEntry> entries, ObjectId objectId, ObjectId? newParentId, int index)
    {
        var map = entries.ToDictionary(e => e.ObjectId);
        var moving = map.GetValueOrDefault(objectId) ?? throw new ArgumentException("Object is not in this outline.", nameof(objectId));
        if (newParentId is { } np)
        {
            if (!map.ContainsKey(np)) throw new ArgumentException("New parent is not in this outline.", nameof(newParentId));
            for (ObjectId? cursor = np; cursor is { } c; cursor = map[c].ParentId)
                if (c == objectId) throw new InvalidOperationException("An object cannot be moved under itself or its own descendant.");
        }
        var oldParent = moving.ParentId;
        var newSiblings = Ordered(map.Values.Where(e => e.ParentId == newParentId && e.ObjectId != objectId)).ToList();
        newSiblings.Insert(Math.Clamp(index, 0, newSiblings.Count), moving with { ParentId = newParentId });
        for (var i = 0; i < newSiblings.Count; i++) map[newSiblings[i].ObjectId] = newSiblings[i] with { SortOrder = i };
        if (oldParent != newParentId)
        {
            var oldSiblings = Ordered(map.Values.Where(e => e.ParentId == oldParent && e.ObjectId != objectId)).ToList();
            for (var i = 0; i < oldSiblings.Count; i++) map[oldSiblings[i].ObjectId] = oldSiblings[i] with { SortOrder = i };
        }
        return map.Values.ToList();
    }
}
