using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TraceForge.Domain;

public sealed record ReqIfObject(string Id, string CanonicalId, string Title, bool IsHeading, int Depth, IReadOnlyDictionary<string, string> Values);
public sealed record ReqIfModule(string Name, IReadOnlyList<ReqIfObject> Objects);
public sealed record ReqIfRelation(string SourceId, string TargetId, string Type);
public sealed record ReqIfExport(string Title, IReadOnlyList<string> AttributeNames, IReadOnlyList<ReqIfModule> Modules, IReadOnlyList<ReqIfRelation> Relations);

/// <summary>What a ReqIF file contained, shaped as a CSV-style table so it goes through the normal import planner and preview.</summary>
public sealed record ReqIfImport(string Title, IReadOnlyList<string> SpecificationNames, int SpecificationIndex, List<List<string>> Table, int ObjectCount, int RelationCount, IReadOnlyList<string> Warnings);

public static class ReqIf
{
    public const string Namespace = "http://www.omg.org/spec/ReqIF/20110401/reqif.xsd";
    private static readonly XNamespace N = Namespace;
    private const int MaxObjects = 200_000;

    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- writing

    /// <summary>ReqIF 1.0 / 1.2 compatible document: one SPECIFICATION per module (outline as SPEC-HIERARCHY), one SPEC-OBJECT per object,
    /// every attribute as a string attribute (the typed definitions stay in TraceForge), and one SPEC-RELATION per link.</summary>
    public static string Write(ReqIfExport export, DateTimeOffset now)
    {
        var stamp = Stamp(now);
        var attrIds = export.AttributeNames.Select((n, i) => (Name: n, Id: "ad-" + (i + 3))).ToList();
        var relTypes = export.Relations.Select(r => r.Type).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
        string RelTypeId(string t) => "srt-" + (relTypes.IndexOf(t) + 1);

        XElement StringDef(string id, string name) => new(N + "ATTRIBUTE-DEFINITION-STRING", new XAttribute("IDENTIFIER", id), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", name),
            new XElement(N + "TYPE", new XElement(N + "DATATYPE-DEFINITION-STRING-REF", "dt-string")));
        XElement StringValue(string id, string value) => new(N + "ATTRIBUTE-VALUE-STRING", new XAttribute("THE-VALUE", value),
            new XElement(N + "DEFINITION", new XElement(N + "ATTRIBUTE-DEFINITION-STRING-REF", id)));

        var specAttributes = new List<XElement> { StringDef("ad-1", "ReqIF.Text"), StringDef("ad-2", "ReqIF.ForeignID") };
        specAttributes.AddRange(attrIds.Select(a => StringDef(a.Id, a.Name)));
        specAttributes.Add(StringDef("ad-heading", "TraceForge.Heading"));

        var objects = new List<XElement>();
        var specifications = new List<XElement>();
        for (var m = 0; m < export.Modules.Count; m++)
        {
            var module = export.Modules[m];
            foreach (var o in module.Objects)
            {
                var values = new List<XElement> { StringValue("ad-1", o.Title), StringValue("ad-2", o.CanonicalId) };
                foreach (var a in attrIds) if (o.Values.TryGetValue(a.Name, out var v) && v.Length > 0) values.Add(StringValue(a.Id, v));
                if (o.IsHeading) values.Add(StringValue("ad-heading", "yes"));
                objects.Add(new XElement(N + "SPEC-OBJECT", new XAttribute("IDENTIFIER", "so-" + o.Id), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", o.CanonicalId),
                    new XElement(N + "TYPE", new XElement(N + "SPEC-OBJECT-TYPE-REF", "sot")), new XElement(N + "VALUES", values)));
            }
            // Outline order with depths becomes nested SPEC-HIERARCHY elements.
            var root = new XElement(N + "CHILDREN");
            var stack = new List<XElement> { root };
            foreach (var o in module.Objects)
            {
                var depth = Math.Min(Math.Max(o.Depth, 0), stack.Count - 1);
                stack.RemoveRange(depth + 1, stack.Count - depth - 1);
                var node = new XElement(N + "SPEC-HIERARCHY", new XAttribute("IDENTIFIER", "sh-" + o.Id), new XAttribute("LAST-CHANGE", stamp),
                    new XElement(N + "OBJECT", new XElement(N + "SPEC-OBJECT-REF", "so-" + o.Id)));
                stack[^1].Add(node);
                var children = new XElement(N + "CHILDREN");
                node.Add(children);
                stack.Add(children);
            }
            foreach (var empty in root.Descendants(N + "CHILDREN").Where(c => !c.HasElements).ToList()) empty.Remove();
            specifications.Add(new XElement(N + "SPECIFICATION", new XAttribute("IDENTIFIER", "spec-" + (m + 1)), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", module.Name),
                new XElement(N + "TYPE", new XElement(N + "SPECIFICATION-TYPE-REF", "st")), root.HasElements ? root : null));
        }

        var relations = export.Relations.Select((r, i) => new XElement(N + "SPEC-RELATION", new XAttribute("IDENTIFIER", "sr-" + (i + 1)), new XAttribute("LAST-CHANGE", stamp),
            new XElement(N + "TYPE", new XElement(N + "SPEC-RELATION-TYPE-REF", RelTypeId(r.Type))),
            new XElement(N + "SOURCE", new XElement(N + "SPEC-OBJECT-REF", "so-" + r.SourceId)),
            new XElement(N + "TARGET", new XElement(N + "SPEC-OBJECT-REF", "so-" + r.TargetId))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(N + "REQ-IF",
                new XElement(N + "THE-HEADER", new XElement(N + "REQ-IF-HEADER", new XAttribute("IDENTIFIER", "header-1"),
                    new XElement(N + "CREATION-TIME", stamp), new XElement(N + "REQ-IF-TOOL-ID", "TraceForge"), new XElement(N + "REQ-IF-VERSION", "1.0"),
                    new XElement(N + "SOURCE-TOOL-ID", "TraceForge"), new XElement(N + "TITLE", export.Title))),
                new XElement(N + "CORE-CONTENT", new XElement(N + "REQ-IF-CONTENT",
                    new XElement(N + "DATATYPES", new XElement(N + "DATATYPE-DEFINITION-STRING", new XAttribute("IDENTIFIER", "dt-string"), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", "String"), new XAttribute("MAX-LENGTH", "32000"))),
                    new XElement(N + "SPEC-TYPES",
                        new XElement(N + "SPEC-OBJECT-TYPE", new XAttribute("IDENTIFIER", "sot"), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", "TraceForge object"), new XElement(N + "SPEC-ATTRIBUTES", specAttributes)),
                        relTypes.Select(t => new XElement(N + "SPEC-RELATION-TYPE", new XAttribute("IDENTIFIER", RelTypeId(t)), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", t))),
                        new XElement(N + "SPECIFICATION-TYPE", new XAttribute("IDENTIFIER", "st"), new XAttribute("LAST-CHANGE", stamp), new XAttribute("LONG-NAME", "TraceForge module"))),
                    new XElement(N + "SPEC-OBJECTS", objects),
                    relations.Any() ? new XElement(N + "SPEC-RELATIONS", relations) : null,
                    new XElement(N + "SPECIFICATIONS", specifications)))));
        return doc.Declaration + "\n" + doc.ToString(SaveOptions.None);
    }

    // ---------------------------------------------------------------- reading

    private static string Clean(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Reads a ReqIF file. DTDs and external entities are refused. Throws FormatException (with a readable message) for files that are not ReqIF.</summary>
    public static ReqIfImport Read(string xml, int specificationIndex = 0)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 200_000_000 });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex) { throw new FormatException("This is not a readable ReqIF/XML file: " + ex.Message, ex); }
        if (doc.Root is null || doc.Root.Name.LocalName != "REQ-IF") throw new FormatException("This XML file is not a ReqIF document (its root element is not REQ-IF).");

        static IEnumerable<XElement> Local(XElement e, string name) => e.Descendants().Where(x => x.Name.LocalName == name);
        static string? Id(XElement e) => (string?)e.Attribute("IDENTIFIER");
        static string Ref(XElement? holder) => holder?.Elements().FirstOrDefault()?.Value.Trim() ?? "";

        var warnings = new List<string>();
        var title = Local(doc.Root, "TITLE").FirstOrDefault()?.Value.Trim() ?? "";

        var enumValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ev in Local(doc.Root, "ENUM-VALUE"))
            if (Id(ev) is { } eid) enumValues[eid] = (string?)ev.Attribute("LONG-NAME") ?? eid;

        var attrNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var def in doc.Root.Descendants().Where(x => x.Name.LocalName.StartsWith("ATTRIBUTE-DEFINITION-", StringComparison.Ordinal)))
            if (Id(def) is { } did) attrNames[did] = (string?)def.Attribute("LONG-NAME") ?? did;

        var objects = new Dictionary<string, (string LongName, Dictionary<string, string> Values)>(StringComparer.Ordinal);
        foreach (var so in Local(doc.Root, "SPEC-OBJECT"))
        {
            if (Id(so) is not { } sid) continue;
            if (objects.Count >= MaxObjects) { warnings.Add($"Only the first {MaxObjects} objects were read."); break; }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in so.Elements().Where(x => x.Name.LocalName == "VALUES").SelectMany(x => x.Elements()).Where(x => x.Name.LocalName.StartsWith("ATTRIBUTE-VALUE-", StringComparison.Ordinal)))
            {
                var defId = Ref(v.Elements().FirstOrDefault(x => x.Name.LocalName == "DEFINITION"));
                var name = attrNames.GetValueOrDefault(defId, defId);
                if (name.Length == 0) continue;
                var kind = v.Name.LocalName["ATTRIBUTE-VALUE-".Length..];
                string text = kind switch
                {
                    "XHTML" => Clean(v.Elements().FirstOrDefault(x => x.Name.LocalName == "THE-VALUE")?.Value ?? ""),
                    "ENUMERATION" => string.Join(", ", v.Elements().Where(x => x.Name.LocalName == "VALUES").SelectMany(x => x.Elements()).Select(x => enumValues.GetValueOrDefault(x.Value.Trim(), x.Value.Trim()))),
                    _ => (string?)v.Attribute("THE-VALUE") ?? "",
                };
                values[name] = text;
            }
            objects[sid] = ((string?)so.Attribute("LONG-NAME") ?? sid, values);
        }

        var specs = Local(doc.Root, "SPECIFICATION").ToList();
        if (specs.Count == 0) throw new FormatException("The ReqIF file contains no SPECIFICATION (document) to import.");
        var specNames = specs.Select(s => (string?)s.Attribute("LONG-NAME") ?? Id(s) ?? "Specification").ToList();
        var spec = specs[Math.Clamp(specificationIndex, 0, specs.Count - 1)];

        // Flatten the hierarchy depth first, numbering as we go.
        var flat = new List<(string Number, string ObjectId)>();
        var missing = 0;
        void Walk(XElement parent, string prefix, int depth)
        {
            if (depth > 64) { warnings.Add("The hierarchy is deeper than 64 levels; deeper entries were skipped."); return; }
            var n = 0;
            foreach (var h in parent.Elements().Where(x => x.Name.LocalName == "CHILDREN").SelectMany(x => x.Elements()).Where(x => x.Name.LocalName == "SPEC-HIERARCHY"))
            {
                var objId = Ref(h.Elements().FirstOrDefault(x => x.Name.LocalName == "OBJECT"));
                if (!objects.ContainsKey(objId)) { missing++; continue; }
                var number = prefix.Length == 0 ? (++n).ToString(CultureInfo.InvariantCulture) : prefix + "." + (++n).ToString(CultureInfo.InvariantCulture);
                flat.Add((number, objId));
                Walk(h, number, depth + 1);
            }
        }
        Walk(spec, "", 0);
        if (missing > 0) warnings.Add($"{missing} hierarchy entr{(missing == 1 ? "y points" : "ies point")} to objects that are not in the file and were skipped.");

        string[] textNames = ["reqif.text", "text", "reqif.chaptername", "chapter name", "name"];
        string[] idNames = ["reqif.foreignid", "id", "identifier"];
        var reserved = new HashSet<string>(textNames.Concat(idNames).Concat(["traceforge.heading", "heading", "number", "links to", "linked from"]), StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();
        foreach (var (_, objId) in flat)
            foreach (var key in objects[objId].Values.Keys)
                if (!reserved.Contains(key) && !extra.Contains(key, StringComparer.OrdinalIgnoreCase)) extra.Add(key);

        var header = new List<string> { "Number", "ID", "Heading", "Text" };
        header.AddRange(extra);
        var table = new List<List<string>> { header };
        foreach (var (number, objId) in flat)
        {
            var (longName, values) = objects[objId];
            string First(string[] names) => names.Select(n => values.GetValueOrDefault(n, "")).FirstOrDefault(v => v.Length > 0) ?? "";
            var text = First(textNames);
            var chapterOnly = values.GetValueOrDefault("reqif.text", "").Length == 0 && values.GetValueOrDefault("text", "").Length == 0 && text.Length > 0;
            var isHeading = values.GetValueOrDefault("TraceForge.Heading", "").Length > 0 || values.GetValueOrDefault("heading", "").Length > 0 || chapterOnly;
            if (text.Length == 0) text = longName;
            var row = new List<string> { number, First(idNames), isHeading ? "yes" : "", text };
            row.AddRange(extra.Select(e => values.GetValueOrDefault(e, "")));
            table.Add(row);
        }
        var relationCount = Local(doc.Root, "SPEC-RELATION").Count();
        if (relationCount > 0) warnings.Add($"{relationCount} relation(s) in the file were not imported (links are made inside TraceForge between a module and its child module).");
        return new ReqIfImport(title, specNames, Math.Clamp(specificationIndex, 0, specs.Count - 1), table, flat.Count, relationCount, warnings);
    }
}
