using System.Net;
using System.Text;

namespace TraceForge.Domain;

/// <summary>A comment under an object in the report. Embeds are the pictures and tables its text asks to show.</summary>
public sealed record ReportComment(string Author, string When, string Body, bool IsReply, bool Resolved, IReadOnlyDictionary<string, EmbedData>? Embeds = null);

public sealed record ReportRow(string Number, string CanonicalId, string Title, int Depth, bool IsHeading, IReadOnlyList<string> Values, string LinksUp, string LinksDown, bool Suspect, IReadOnlyDictionary<string, EmbedData>? Embeds = null, IReadOnlyList<ReportComment>? Comments = null);

public sealed record ReportModule(string Name, IReadOnlyList<string> AttributeNames, IReadOnlyList<ReportRow> Rows);

/// <summary>Renders modules as one self-contained HTML document (no scripts, no external files) that prints cleanly. All text is HTML-encoded.</summary>
public static class HtmlReport
{
    public static string Render(string title, string generated, IReadOnlyList<ReportModule> modules)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(title)).Append("</title><style>")
          .Append(":root{color-scheme:light dark}body{font:14px/1.45 system-ui,sans-serif;margin:2rem auto;max-width:1100px;padding:0 1rem}")
          .Append("h1{font-size:1.6rem}h2{margin-top:2.5rem;font-size:1.25rem}table{border-collapse:collapse;width:100%;margin-top:.5rem}")
          .Append("th,td{border:1px solid #8885;padding:.3rem .5rem;text-align:left;vertical-align:top}th{background:#8882}")
          .Append("tr.heading td{font-weight:700;background:#8881}.num{white-space:nowrap}.id{white-space:nowrap;font-family:ui-monospace,monospace}")
          .Append(".suspect{color:#b45309;font-weight:600}.meta{color:#888}nav li{margin:.1rem 0}table.md{margin:.4rem 0;width:auto}figure{margin:.4rem 0}figcaption{color:#888;font-size:.9em}tr.comments td{background:#8881;font-size:.95em}div.c{margin:.3rem 0}div.c.reply{margin-left:1.5rem}")
          .Append("@media print{body{margin:0;max-width:none}h2{break-before:page}h2:first-of-type{break-before:auto}tr{break-inside:avoid}a{color:inherit;text-decoration:none}}")
          .Append("</style></head><body>\n<h1>").Append(E(title)).Append("</h1>\n<p class=\"meta\">Generated ").Append(E(generated)).Append("</p>\n");
        if (modules.Count > 1)
        {
            sb.Append("<nav><strong>Contents</strong><ol>");
            for (var i = 0; i < modules.Count; i++) sb.Append("<li><a href=\"#m").Append(i).Append("\">").Append(E(modules[i].Name)).Append("</a> (").Append(modules[i].Rows.Count(r => !r.IsHeading)).Append(" objects)</li>");
            sb.Append("</ol></nav>\n");
        }
        for (var i = 0; i < modules.Count; i++)
        {
            var m = modules[i];
            sb.Append("<h2 id=\"m").Append(i).Append("\">").Append(E(m.Name)).Append("</h2>\n<table><thead><tr><th>Number</th><th>ID</th><th>Text</th>");
            foreach (var a in m.AttributeNames) sb.Append("<th>").Append(E(a)).Append("</th>");
            sb.Append("<th>Linked from</th><th>Links to</th></tr></thead><tbody>\n");
            foreach (var r in m.Rows)
            {
                sb.Append(r.IsHeading ? "<tr class=\"heading\">" : "<tr>")
                  .Append("<td class=\"num\">").Append(E(r.Number)).Append("</td><td class=\"id\">").Append(E(r.CanonicalId)).Append("</td>")
                  .Append("<td style=\"padding-left:").Append((0.5 + r.Depth * 1.2).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("rem\">").Append(Markup.ToHtml(r.Title, r.Embeds)).Append("</td>");
                for (var c = 0; c < m.AttributeNames.Count; c++) sb.Append("<td>").Append(E(c < r.Values.Count ? r.Values[c] : "")).Append("</td>");
                sb.Append("<td").Append(r.Suspect ? " class=\"suspect\"" : "").Append('>').Append(E(r.LinksUp)).Append(r.Suspect ? " (suspect)" : "").Append("</td><td>").Append(E(r.LinksDown)).Append("</td></tr>\n");
                if (r.Comments is { Count: > 0 })
                {
                    sb.Append("<tr class=\"comments\"><td colspan=\"").Append(5 + m.AttributeNames.Count).Append("\"><strong>Comments</strong>");
                    foreach (var c in r.Comments)
                        sb.Append("<div class=\"c").Append(c.IsReply ? " reply" : "").Append("\"><span class=\"meta\">").Append(E(c.Author)).Append(" \u00B7 ").Append(E(c.When))
                          .Append(!c.IsReply && c.Resolved ? " \u00B7 resolved" : "").Append("</span><div>").Append(Markup.ToHtml(c.Body, c.Embeds)).Append("</div></div>");
                    sb.Append("</td></tr>\n");
                }
            }
            sb.Append("</tbody></table>\n");
        }
        return sb.Append("</body></html>\n").ToString();
    }
}
