using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class CsvTests
{
    [Fact] public void Quotes_embedded_newlines_and_doubled_quotes_are_parsed()
    {
        var rows = Csv.Parse("ID,Text\r\nR-1,\"He said \"\"go\"\", then\nleft\"\r\nR-2,plain\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("He said \"go\", then\nleft", rows[1][1]);
        Assert.Equal(new[] { "R-2", "plain" }, rows[2]);
    }

    [Theory] [InlineData("a;b;c\n1;2;3", 3)] [InlineData("a\tb\tc\n1\t2\t3", 3)] [InlineData("a,b,c\n1,2,3", 3)]
    public void Delimiter_is_detected(string text, int columns) => Assert.Equal(columns, Csv.Parse(text)[1].Count);

    [Fact] public void Bom_blank_lines_and_missing_final_newline_are_handled()
    {
        var rows = Csv.Parse("﻿a,b\n\n1,2");
        Assert.Equal(2, rows.Count); Assert.Equal("a", rows[0][0]); Assert.Equal(new[] { "1", "2" }, rows[1]);
    }

    [Fact] public void Format_then_parse_round_trips_awkward_cells()
    {
        var original = new[] { new[] { "a,b", "say \"hi\"", "line1\r\nline2", "" }, new[] { "x", "", "y", "z" } };
        var parsed = Csv.Parse(Csv.Format(original));
        Assert.Equal(original.Select(r => r.ToList()), parsed);
    }

    [Theory] [InlineData("=1+1", "'=1+1")] [InlineData("@cmd", "'@cmd")] [InlineData("-5", "-5")] [InlineData("+3.5", "+3.5")] [InlineData("-abc", "'-abc")] [InlineData("plain", "plain")] [InlineData("", "")]
    public void Formula_injection_is_neutralised_and_numbers_are_left_alone(string cell, string expected)
    {
        Assert.Equal(expected, CsvGuard.Protect(cell));
        Assert.Equal(cell, CsvGuard.Unprotect(CsvGuard.Protect(cell)));
    }
}

public sealed class ImportPlannerTests
{
    private static readonly AttributeDefinition Priority = AttributeDefinition.Create("Priority", AttributeDataType.Enumeration, false, new[] { "low", "high" });
    private static readonly AttributeDefinition Owner = AttributeDefinition.Create("Owner", AttributeDataType.Text, true, Array.Empty<string>());
    private static readonly AttributeDefinition Spare = AttributeDefinition.Create("Cost", AttributeDataType.Integer, false, Array.Empty<string>());

    private static ImportContext Ctx(IEnumerable<ExistingObject>? existing = null, bool createUnknown = false, IEnumerable<string>? taken = null,
        Dictionary<string, LinkTarget>? targets = null, IEnumerable<ObjectId>? childModules = null, IEnumerable<(ObjectId, ObjectId, RelationshipType)>? links = null) =>
        new([Priority, Owner], [Spare], (existing ?? []).ToList(), new HashSet<string>(taken ?? [], StringComparer.OrdinalIgnoreCase),
            targets ?? new Dictionary<string, LinkTarget>(StringComparer.OrdinalIgnoreCase), new HashSet<ObjectId>(childModules ?? []),
            new HashSet<(ObjectId, ObjectId, RelationshipType)>(links ?? []), createUnknown);

    private static ImportPlan Plan(string csv, ImportContext c) => ImportPlanner.Plan(Csv.Parse(csv), c);

    [Fact] public void New_rows_become_creates_with_hierarchy_from_numbers_and_normalised_values()
    {
        var plan = Plan("Number,Text,Heading,Priority,Owner\n1,Intro,yes,,team\n1.1,Boot time,,HIGH,team\n1.2,Logging,,low,team\n2,Other,,,team", Ctx());
        Assert.All(plan.Rows, r => Assert.Equal(ImportAction.Create, r.Action));
        Assert.True(plan.Rows[0].IsHeading);
        Assert.Equal(0, plan.Rows[1].ParentRowIndex); Assert.Equal(0, plan.Rows[2].ParentRowIndex); Assert.Null(plan.Rows[3].ParentRowIndex);
        Assert.Equal("high", plan.Rows[1].Values[Priority.Id.ToString()]);
        Assert.Empty(plan.Issues);
    }

    [Fact] public void Existing_ids_update_only_what_changed_and_blank_cells_never_clear()
    {
        var id = ObjectId.New();
        var existing = new ExistingObject(id, "SRS-001", "Boot time", new Dictionary<string, string> { [Priority.Id.ToString()] = "low", [Owner.Id.ToString()] = "team" });
        var changed = Plan("ID,Text,Priority,Owner\nSRS-001,Boot time under 2 s,high,", Ctx([existing]));
        var row = Assert.Single(changed.Rows);
        Assert.Equal(ImportAction.Update, row.Action); Assert.Equal(id, row.ExistingId);
        Assert.Equal("high", row.Values[Priority.Id.ToString()]); Assert.False(row.Values.ContainsKey(Owner.Id.ToString()));
        var same = Plan("ID,Text,Priority\nsrs-001,Boot time,low", Ctx([existing]));
        Assert.Equal(ImportAction.Unchanged, Assert.Single(same.Rows).Action);
    }

    [Fact] public void Conflicts_and_bad_rows_are_skipped_with_errors()
    {
        var plan = Plan("ID,Text\nOTHER-1,From another module\nNEW-1,Fine\nNEW-1,Duplicate\n,\nNEW-2,", Ctx(taken: ["OTHER-1"]));
        Assert.Equal(new[] { ImportAction.Skip, ImportAction.Create, ImportAction.Skip, ImportAction.Skip }, plan.Rows.Select(r => r.Action));
        Assert.Equal(3, plan.Issues.Count(i => i.IsError));
    }

    [Fact] public void Invalid_values_are_ignored_with_a_warning_and_a_missing_text_column_is_an_error()
    {
        var plan = Plan("Text,Priority\nBoot,urgent", Ctx());
        Assert.Empty(Assert.Single(plan.Rows).Values);
        Assert.Contains(plan.Issues, i => !i.IsError && i.Message.Contains("Priority"));
        var none = Plan("Name\nx", Ctx());
        Assert.Empty(none.Rows); Assert.Contains(none.Issues, i => i.IsError && i.Message.Contains("Text column"));
    }

    [Fact] public void Unknown_columns_are_ignored_or_created_and_unassigned_attributes_are_assigned()
    {
        var csv = "Text,Cost,Notes\nBoot,5,remember";
        var ignored = Plan(csv, Ctx());
        Assert.Empty(ignored.NewAttributes); Assert.Equal(new[] { Spare }, ignored.AssignAttributes);
        Assert.Contains(ignored.Issues, i => i.Message.Contains("Notes"));
        var created = Plan(csv, Ctx(createUnknown: true));
        var notes = Assert.Single(created.NewAttributes); Assert.Equal("Notes", notes.Name);
        Assert.Equal("remember", created.Rows[0].Values[notes.Id.ToString()]);
    }

    [Fact] public void Links_resolve_only_to_direct_child_module_objects_and_skip_existing_ones()
    {
        var child = ObjectId.New(); var other = ObjectId.New(); var target = ObjectId.New(); var wrong = ObjectId.New(); var src = ObjectId.New();
        var targets = new Dictionary<string, LinkTarget>(StringComparer.OrdinalIgnoreCase) { ["SDD-1"] = new(target, child), ["XXX-1"] = new(wrong, other) };
        var existing = new ExistingObject(src, "SRS-001", "Boot", new Dictionary<string, string>());
        var plan = Plan("ID,Text,Links to\nSRS-001,Boot,Satisfies:SDD-1; XXX-1; NOPE\n,New one,SDD-1", Ctx([existing], targets: targets, childModules: [child]));
        Assert.Equal(2, plan.Links.Count);
        Assert.Equal((0, target, RelationshipType.Satisfies), (plan.Links[0].RowIndex, plan.Links[0].TargetId, plan.Links[0].Type));
        Assert.Equal((1, target, RelationshipType.Decomposes), (plan.Links[1].RowIndex, plan.Links[1].TargetId, plan.Links[1].Type));
        Assert.Equal(2, plan.Issues.Count(i => !i.IsError));
        var again = Plan("ID,Text,Links to\nSRS-001,Boot,Satisfies:SDD-1", Ctx([existing], targets: targets, childModules: [child], links: [(src, target, RelationshipType.Satisfies)]));
        Assert.Empty(again.Links);
    }

    [Fact] public void Formula_guard_is_removed_from_imported_cells()
    {
        var plan = Plan("Text\n'=SUM(A1)", Ctx());
        Assert.Equal("=SUM(A1)", Assert.Single(plan.Rows).Text);
    }
}
