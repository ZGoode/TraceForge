using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TraceForge.Domain;

/// <summary>Minimal RFC 4180 CSV reading and writing (quotes, doubled quotes, embedded newlines, CRLF or LF, optional BOM).</summary>
public static class Csv
{
    /// <summary>Parses CSV text into rows of cells. The delimiter (comma, semicolon or tab) is detected from the first line. Blank lines are skipped.</summary>
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var delimiter = DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false; var cellStarted = false;
        void EndCell() { row.Add(cell.ToString()); cell.Clear(); cellStarted = false; }
        void EndRow()
        {
            EndCell();
            if (row.Any(c => c.Length > 0)) rows.Add(row);
            row = new List<string>();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"' && !cellStarted) { inQuotes = true; cellStarted = true; }
            else if (ch == delimiter) EndCell();
            else if (ch == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (ch == '\n') EndRow();
            else { cell.Append(ch); cellStarted = true; }
        }
        if (cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        int comma = 0, semicolon = 0, tab = 0; var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (!inQuotes)
            {
                if (ch == '\n' || ch == '\r') break;
                if (ch == ',') comma++; else if (ch == ';') semicolon++; else if (ch == '\t') tab++;
            }
        }
        if (tab > comma && tab >= semicolon) return '\t';
        if (semicolon > comma) return ';';
        return ',';
    }

    /// <summary>Formats rows as CSV with comma delimiters and CRLF line ends. Cells containing commas, quotes or line breaks are quoted.</summary>
    public static string Format(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Escape)));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Escape(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
}

/// <summary>Protects exported cells from spreadsheet formula injection and undoes that on import.
/// A cell that starts with = + - @ (or a tab or CR) gets a leading apostrophe, except plain numbers.</summary>
public static class CsvGuard
{
    private const string Triggers = "=+-@\t\r";
    private static readonly Regex Number = new(@"^[+-]?\d+(\.\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Protect(string cell) => cell.Length > 0 && Triggers.Contains(cell[0]) && !Number.IsMatch(cell) ? "'" + cell : cell;

    public static string Unprotect(string cell) => cell.Length > 1 && cell[0] == '\'' && Triggers.Contains(cell[1]) ? cell[1..] : cell;
}

// ---------- import planning ----------

public enum ImportAction { Create, Update, Unchanged, Skip }

public sealed record ExistingObject(ObjectId Id, string CanonicalId, string Title, IReadOnlyDictionary<string, string> Values);

public sealed record LinkTarget(ObjectId Id, ObjectId? ModuleId);

/// <summary>Everything the planner must know about the project. Nothing here is modified by planning.</summary>
public sealed record ImportContext(
    IReadOnlyList<AttributeDefinition> ModuleAttributes,
    IReadOnlyList<AttributeDefinition> OtherAttributes,
    IReadOnlyList<ExistingObject> ModuleObjects,
    IReadOnlySet<string> TakenIds,
    IReadOnlyDictionary<string, LinkTarget> LinkTargets,
    IReadOnlySet<ObjectId> ChildModuleIds,
    IReadOnlySet<(ObjectId Source, ObjectId Target, RelationshipType Type)> ExistingLinks,
    bool CreateUnknownAttributes);

public sealed record PlannedRow(int RowNumber, ImportAction Action, string? CanonicalId, ObjectId? ExistingId, string Number, string Text, bool IsHeading,
    int? ParentRowIndex, IReadOnlyDictionary<string, string> Values);

public sealed record PlannedLink(int RowIndex, ObjectId TargetId, RelationshipType Type, string TargetLabel);

public sealed record ImportIssue(int RowNumber, bool IsError, string Message);

public sealed record ImportPlan(
    IReadOnlyList<PlannedRow> Rows, IReadOnlyList<PlannedLink> Links, IReadOnlyList<AttributeDefinition> NewAttributes,
    IReadOnlyList<AttributeDefinition> AssignAttributes, IReadOnlyList<ImportIssue> Issues)
{
    public int Count(ImportAction action) => Rows.Count(r => r.Action == action);
    public bool HasWork => Rows.Any(r => r.Action is ImportAction.Create or ImportAction.Update) || Links.Count > 0;
}

public static class ImportPlanner
{
    private static readonly Regex NumberPattern = new(@"^\d+(\.\d+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] NumberNames = ["number", "no", "no.", "outline", "section"];
    private static readonly string[] IdNames = ["id", "identifier", "object id", "canonical id"];
    private static readonly string[] TextNames = ["text", "title", "object text", "requirement", "object"];
    private static readonly string[] HeadingNames = ["heading", "is heading"];
    private static readonly string[] LinksToNames = ["links to", "links"];
    private static readonly string[] IgnoredNames = ["linked from", "revision", "rev"];

    private enum Role { Number, Id, Text, Heading, LinksTo, Ignored, Attribute, Unknown }
    private sealed record Column(int Index, Role Role, AttributeDefinition? Attribute);

    public static ImportPlan Plan(IReadOnlyList<IReadOnlyList<string>> table, ImportContext context)
    {
        var issues = new List<ImportIssue>();
        var newAttributes = new List<AttributeDefinition>();
        var assign = new List<AttributeDefinition>();
        if (table.Count == 0) { issues.Add(new(0, true, "The file is empty.")); return Empty(issues); }

        // ----- header -----
        var byName = context.ModuleAttributes.Concat(context.OtherAttributes).GroupBy(a => a.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var moduleIds = context.ModuleAttributes.Select(a => a.Id).ToHashSet();
        var columns = new List<Column>();
        var usedAttributes = new HashSet<ObjectId>();
        var seenRoles = new HashSet<Role>();
        for (var i = 0; i < table[0].Count; i++)
        {
            var header = table[0][i].Trim();
            var key = header.ToLowerInvariant();
            if (key.Length == 0) continue;
            Role role =
                NumberNames.Contains(key) ? Role.Number : IdNames.Contains(key) ? Role.Id : TextNames.Contains(key) ? Role.Text :
                HeadingNames.Contains(key) ? Role.Heading : LinksToNames.Contains(key) ? Role.LinksTo : IgnoredNames.Contains(key) ? Role.Ignored : Role.Unknown;
            if (role is not (Role.Unknown or Role.Ignored))
            {
                if (!seenRoles.Add(role)) { issues.Add(new(1, false, $"Column '{header}' repeats an earlier column and is ignored.")); continue; }
                columns.Add(new Column(i, role, null)); continue;
            }
            if (role == Role.Ignored) continue;
            if (byName.TryGetValue(key, out var definition))
            {
                if (!usedAttributes.Add(definition.Id)) { issues.Add(new(1, false, $"Column '{header}' repeats an earlier attribute column and is ignored.")); continue; }
                if (!moduleIds.Contains(definition.Id)) assign.Add(definition);
                columns.Add(new Column(i, Role.Attribute, definition));
            }
            else if (context.CreateUnknownAttributes)
            {
                var created = AttributeDefinition.Create(header, AttributeDataType.Text, false, Array.Empty<string>());
                byName[key] = created; usedAttributes.Add(created.Id); newAttributes.Add(created);
                columns.Add(new Column(i, Role.Attribute, created));
            }
            else issues.Add(new(1, false, $"Column '{header}' is not an attribute of this module and is ignored (tick the option to create it)."));
        }
        var textColumn = columns.FirstOrDefault(c => c.Role == Role.Text);
        if (textColumn is null)
        {
            issues.Add(new(1, true, "No Text column found. The first row must contain headers, one of them 'Text' (or Title, Object text, Requirement)."));
            return Empty(issues);
        }
        string Cell(IReadOnlyList<string> row, Role role) { var c = columns.FirstOrDefault(x => x.Role == role); return c is null || c.Index >= row.Count ? "" : CsvGuard.Unprotect(row[c.Index].Trim()); }

        // ----- rows -----
        var existingById = context.ModuleObjects.ToDictionary(o => o.CanonicalId, StringComparer.OrdinalIgnoreCase);
        var rows = new List<PlannedRow>();
        var links = new List<PlannedLink>();
        var numberToRow = new Dictionary<string, int>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedLinkKeys = new HashSet<(int, ObjectId, RelationshipType)>();
        var requiredGaps = new Dictionary<string, int>();
        var hasNumbers = columns.Any(c => c.Role == Role.Number);

        for (var r = 1; r < table.Count; r++)
        {
            var row = table[r];
            var rowNumber = r + 1;
            var text = Cell(row, Role.Text);
            if (text.Length == 0) { issues.Add(new(rowNumber, true, "Text is empty; row skipped.")); rows.Add(Skip(rowNumber)); continue; }
            var id = Cell(row, Role.Id);
            var heading = Cell(row, Role.Heading).ToLowerInvariant() is "yes" or "y" or "true" or "1" or "x" or "heading";

            ImportAction action; ExistingObject? existing = null; string? canonical = null;
            if (id.Length == 0) { action = ImportAction.Create; }
            else if (!seenIds.Add(id)) { issues.Add(new(rowNumber, true, $"ID {id} appears more than once in the file; row skipped.")); rows.Add(Skip(rowNumber)); continue; }
            else if (existingById.TryGetValue(id, out existing)) { action = ImportAction.Update; canonical = existing.CanonicalId; }
            else if (context.TakenIds.Contains(id)) { issues.Add(new(rowNumber, true, $"ID {id} already belongs to an object in another module or a deleted object; row skipped.")); rows.Add(Skip(rowNumber)); continue; }
            else { action = ImportAction.Create; canonical = id; }

            // hierarchy (only meaningful for new objects; existing objects keep their position)
            var number = Cell(row, Role.Number);
            int? parentRow = null;
            if (number.Length > 0)
            {
                if (!NumberPattern.IsMatch(number)) issues.Add(new(rowNumber, false, $"Number '{number}' is not like 1 or 1.2.3; the object is placed at top level."));
                else
                {
                    numberToRow.TryAdd(number, rows.Count);
                    var dot = number.LastIndexOf('.');
                    if (dot > 0)
                    {
                        if (numberToRow.TryGetValue(number[..dot], out var p)) parentRow = p;
                        else if (action == ImportAction.Create) issues.Add(new(rowNumber, false, $"Parent {number[..dot]} was not found earlier in the file; the object is placed at top level."));
                    }
                }
            }
            else if (hasNumbers && action == ImportAction.Create) issues.Add(new(rowNumber, false, "No number; the object is placed at top level."));

            // attribute values
            var values = new Dictionary<string, string>();
            var changed = existing is null ? text.Length > 0 : existing.Title != text;
            foreach (var column in columns.Where(c => c.Role == Role.Attribute))
            {
                var raw = column.Index < row.Count ? CsvGuard.Unprotect(row[column.Index].Trim()) : "";
                var definition = column.Attribute!;
                if (raw.Length == 0)
                {
                    if (action == ImportAction.Create && definition.IsRequired && moduleIds.Contains(definition.Id)) requiredGaps[definition.Name] = requiredGaps.GetValueOrDefault(definition.Name) + 1;
                    continue; // a blank cell never clears an existing value
                }
                if (!definition.TryNormalize(raw, out var normalized, out var error)) { issues.Add(new(rowNumber, false, $"'{raw}' is not valid for {definition.Name} ({error}); that value is ignored.")); continue; }
                if (existing is not null && existing.Values.GetValueOrDefault(definition.Id.ToString()) == normalized) continue;
                values[definition.Id.ToString()] = normalized; changed = true;
            }
            if (action == ImportAction.Update && !changed) action = ImportAction.Unchanged;
            var index = rows.Count;
            rows.Add(new PlannedRow(rowNumber, action, canonical, existing?.Id, number, text, heading, action == ImportAction.Create ? parentRow : null, values));

            // links
            var linksCell = Cell(row, Role.LinksTo);
            if (linksCell.Length > 0)
            {
                var sourceId = existing?.Id;
                foreach (var token in linksCell.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var type = RelationshipType.Decomposes; var target = token;
                    var colon = token.IndexOf(':');
                    if (colon > 0 && Enum.TryParse<RelationshipType>(token[..colon].Trim(), true, out var parsed)) { type = parsed; target = token[(colon + 1)..].Trim(); }
                    if (!context.LinkTargets.TryGetValue(target, out var found)) { issues.Add(new(rowNumber, false, $"Link target {target} does not exist; link ignored.")); continue; }
                    if (found.ModuleId is null || !context.ChildModuleIds.Contains(found.ModuleId.Value)) { issues.Add(new(rowNumber, false, $"Link target {target} is not in a direct child module; link ignored.")); continue; }
                    if (sourceId is { } s && context.ExistingLinks.Contains((s, found.Id, type))) continue; // already linked
                    if (!plannedLinkKeys.Add((index, found.Id, type))) continue;                              // repeated in this row
                    links.Add(new PlannedLink(index, found.Id, type, $"{type}: {target}"));
                }
            }
        }
        foreach (var (name, count) in requiredGaps)
            issues.Add(new(0, false, $"{count} new object(s) have no value for the required attribute {name} (they will be flagged in the table)."));
        return new ImportPlan(rows, links, newAttributes, assign, issues);

        static PlannedRow Skip(int rowNumber) => new(rowNumber, ImportAction.Skip, null, null, "", "", false, null, new Dictionary<string, string>());
    }

    private static ImportPlan Empty(List<ImportIssue> issues) => new([], [], [], [], issues);
}

/// <summary>What an import changed, so it can be undone.</summary>
public sealed record ImportOutcome(IReadOnlyList<ObjectId> Created, IReadOnlyList<(ObjectId Id, long PreviousRevision)> Updated, IReadOnlyList<ObjectId> LinkIds, int NewAttributes);

/// <summary>A change to an existing object made by an import: new text (null = keep) and attribute values to set (attribute id to canonical value).</summary>
public sealed record ObjectUpdate(ObjectId ObjectId, string? NewTitle, IReadOnlyDictionary<string, string> Values);
