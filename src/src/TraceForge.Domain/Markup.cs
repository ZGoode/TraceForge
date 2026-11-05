using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TraceForge.Domain;

public sealed record MarkupRun(string Text, bool Bold, bool Italic);
public sealed record MarkupLine(bool IsBullet, IReadOnlyList<MarkupRun> Runs);

/// <summary>A picture or table taken from one of the object's attachments, ready to show. Rows are used for tables, Bytes for images.</summary>
public sealed record EmbedData(bool IsImage, string Mime, byte[]? Bytes, IReadOnlyList<IReadOnlyList<string>>? Rows);

public abstract record MarkupBlock;
/// <summary>Consecutive ordinary lines (text, bullets).</summary>
public sealed record ParagraphBlock(IReadOnlyList<MarkupLine> Lines) : MarkupBlock;
/// <summary>A pipe table. When HasHeader is set the first row is the header. Cells carry the same inline formatting as text.</summary>
public sealed record TableBlock(bool HasHeader, IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkupRun>>> Rows) : MarkupBlock;
/// <summary>A line of the form ![caption](attach:file name) that shows the object's attachment of that name (a picture, or a CSV/TSV file as a table).</summary>
public sealed record EmbedBlock(string Alt, string Name) : MarkupBlock;

/// <summary>Lightweight formatting for object text: **bold**, *italic*, lines starting with "- " or "* " are bullets, line breaks are kept.
/// A marker without a closing partner on the same line is shown literally; a backslash before * shows it literally. The stored text is never changed.</summary>
public static class Markup
{
    public const int MaxEmbedImageBytes = 8 * 1024 * 1024;
    public const int MaxEmbedTableBytes = 2 * 1024 * 1024;
    public const int MaxEmbedTableRows = 200;
    public const int MaxEmbedTableColumns = 20;

    private static readonly Regex EmbedLine =
        new(@"^\s*!\[(?<alt>[^\]]*)\]\(attach:(?<name>[^)]+)\)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>MIME type for the picture types TraceForge shows, or null. SVG is deliberately not included.</summary>
    public static string? ImageMime(string fileName) => System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".webp" => "image/webp", _ => null,
    };

    public static bool IsTableFile(string fileName) => System.IO.Path.GetExtension(fileName).ToLowerInvariant() is ".csv" or ".tsv";

    /// <summary>Splits text into paragraphs, pipe tables and embed lines. Text without any of the last two gives one paragraph.</summary>
    public static IReadOnlyList<MarkupBlock> ParseBlocks(string? text)
    {
        var blocks = new List<MarkupBlock>();
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new ParagraphBlock(Parse(string.Join("\n", paragraph))));
            paragraph.Clear();
        }
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var embed = EmbedLine.Match(line);
            if (embed.Success)
            {
                FlushParagraph();
                blocks.Add(new EmbedBlock(embed.Groups["alt"].Value.Trim(), embed.Groups["name"].Value.Trim()));
                continue;
            }
            if (line.TrimStart().StartsWith('|'))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) tableLines.Add(lines[i++]);
                i--;
                FlushParagraph();
                blocks.Add(ToTable(tableLines));
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph();
        if (blocks.Count == 0) blocks.Add(new ParagraphBlock(Parse("")));
        return blocks;
    }

    private static List<string> SplitRow(string line)
    {
        var cells = line.Trim().Split('|').Select(c => c.Trim()).ToList();
        if (cells.Count > 0 && cells[0].Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    private static TableBlock ToTable(List<string> lines)
    {
        var rows = lines.Select(SplitRow).ToList();
        var hasHeader = rows.Count >= 2 && rows[1].Count > 0 && rows[1].All(c => c.Length > 0 && c.All(ch => ch is '-' or ':') && c.Contains('-'));
        if (hasHeader) rows.RemoveAt(1);
        else rows.RemoveAll(r => r.Count > 0 && r.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':') && c.Contains('-'))); // stray separator rows are not data
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var cells = rows.Select(r => (IReadOnlyList<IReadOnlyList<MarkupRun>>)Enumerable.Range(0, width).Select(c => Runs(c < r.Count ? r[c] : "")).Select(x => (IReadOnlyList<MarkupRun>)x).ToList()).ToList();
        return new TableBlock(hasHeader, cells);
    }

    /// <summary>Names of the attachments the text asks to show, in order of appearance.</summary>
    public static IReadOnlyList<string> EmbedNames(string? text) => ParseBlocks(text).OfType<EmbedBlock>().Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<MarkupLine> Parse(string? text)
    {
        var lines = new List<MarkupLine>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            var bullet = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);
            lines.Add(new MarkupLine(bullet, Runs(bullet ? trimmed[2..] : line)));
        }
        return lines;
    }

    private static List<MarkupRun> Runs(string s)
    {
        var runs = new List<MarkupRun>();
        var sb = new StringBuilder();
        bool bold = false, italic = false;
        void Flush() { if (sb.Length > 0) { runs.Add(new MarkupRun(sb.ToString(), bold, italic)); sb.Clear(); } }
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '*') { sb.Append('*'); i++; continue; }
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*' && (bold || HasCloser(s, i + 2, "**")))
            { Flush(); bold = !bold; i++; continue; }
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*') { sb.Append("**"); i++; continue; } // a "**" with no partner is literal
            if (s[i] == '*' && (italic || HasCloser(s, i + 1, "*")))
            { Flush(); italic = !italic; continue; }
            sb.Append(s[i]);
        }
        Flush();
        return runs;
    }

    // A closer is a later marker that is not escaped. For "*" a "**" pair is not a closer.
    private static bool HasCloser(string s, int from, string marker)
    {
        for (var i = from; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (marker == "**" ? s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*' : s[i] == '*' && !(i + 1 < s.Length && s[i + 1] == '*')) return true;
            if (marker == "*" && s[i] == '*') i++; // skip over a "**" pair
        }
        return false;
    }

    /// <summary>Text without markers, lines joined by " ⏎ " (for grid cells and CSV previews). Tables and embeds become short placeholders.</summary>
    public static string Plain(string? text)
    {
        var parts = new List<string>();
        foreach (var block in ParseBlocks(text))
            switch (block)
            {
                case ParagraphBlock p:
                    parts.AddRange(p.Lines.Select(l => (l.IsBullet ? "\u2022 " : "") + string.Concat(l.Runs.Select(r => r.Text))).Where(l => l.Length > 0));
                    break;
                case TableBlock t:
                    parts.Add($"[table {t.Rows.Count}\u00D7{(t.Rows.Count == 0 ? 0 : t.Rows[0].Count)}]");
                    break;
                case EmbedBlock e:
                    parts.Add(e.Alt.Length > 0 ? $"[{(IsTableFile(e.Name) ? "table" : "image")}: {e.Alt}]" : $"[{e.Name}]");
                    break;
            }
        return string.Join(" \u23CE ", parts);
    }

    /// <summary>HTML for the text: every piece of text is encoded, so no markup from the object can reach the page. Embeds are shown only when
    /// the caller supplies them (keyed by attachment file name); pictures become data: URIs so the page stays one self-contained file.</summary>
    public static string ToHtml(string? text, IReadOnlyDictionary<string, EmbedData>? embeds = null)
    {
        var sb = new StringBuilder();
        foreach (var block in ParseBlocks(text))
            switch (block)
            {
                case ParagraphBlock p: AppendParagraph(sb, p); break;
                case TableBlock t: AppendTable(sb, t.HasHeader, t.Rows.Select(r => r.Select(c => RunsHtml(c)).ToList()).ToList()); break;
                case EmbedBlock e: AppendEmbed(sb, e, embeds); break;
            }
        return sb.ToString();
    }

    private static string RunsHtml(IEnumerable<MarkupRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var r in runs)
        {
            var t = WebUtility.HtmlEncode(r.Text);
            if (r.Italic) t = "<em>" + t + "</em>";
            if (r.Bold) t = "<strong>" + t + "</strong>";
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static void AppendParagraph(StringBuilder sb, ParagraphBlock p)
    {
        var inList = false;
        var first = true;
        foreach (var line in p.Lines)
        {
            if (line.IsBullet && !inList) { sb.Append("<ul>"); inList = true; }
            if (!line.IsBullet && inList) { sb.Append("</ul>"); inList = false; }
            else if (!line.IsBullet && !first) sb.Append("<br>");
            if (line.IsBullet) sb.Append("<li>");
            sb.Append(RunsHtml(line.Runs));
            if (line.IsBullet) sb.Append("</li>");
            first = false;
        }
        if (inList) sb.Append("</ul>");
    }

    private static void AppendTable(StringBuilder sb, bool hasHeader, List<List<string>> rows)
    {
        sb.Append("<table class=\"md\">");
        for (var r = 0; r < rows.Count; r++)
        {
            var tag = hasHeader && r == 0 ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in rows[r]) sb.Append('<').Append(tag).Append('>').Append(cell).Append("</").Append(tag).Append('>');
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    private static void AppendEmbed(StringBuilder sb, EmbedBlock e, IReadOnlyDictionary<string, EmbedData>? embeds)
    {
        if (embeds is null || !embeds.TryGetValue(e.Name, out var data))
        { sb.Append("<p><em>[attachment not found: ").Append(WebUtility.HtmlEncode(e.Name)).Append("]</em></p>"); return; }
        if (data.IsImage && data.Bytes is { } bytes && ImageMime(e.Name) is { } mime)
        {
            if (bytes.Length > MaxEmbedImageBytes) { sb.Append("<p><em>[image too large to embed: ").Append(WebUtility.HtmlEncode(e.Name)).Append("]</em></p>"); return; }
            sb.Append("<figure><img style=\"max-width:100%\" alt=\"").Append(WebUtility.HtmlEncode(e.Alt)).Append("\" src=\"data:").Append(mime).Append(";base64,").Append(Convert.ToBase64String(bytes)).Append("\">");
            if (e.Alt.Length > 0) sb.Append("<figcaption>").Append(WebUtility.HtmlEncode(e.Alt)).Append("</figcaption>");
            sb.Append("</figure>");
        }
        else if (!data.IsImage && data.Rows is { } rows)
        {
            if (e.Alt.Length > 0) sb.Append("<p><strong>").Append(WebUtility.HtmlEncode(e.Alt)).Append("</strong></p>");
            AppendTable(sb, true, rows.Select(r => r.Select(c => WebUtility.HtmlEncode(c)).ToList()).ToList());
        }
        else sb.Append("<p><em>[cannot show: ").Append(WebUtility.HtmlEncode(e.Name)).Append("]</em></p>");
    }
}
