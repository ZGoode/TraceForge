using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class ReportCommentTests
{
    [Fact] public void Comments_appear_under_their_row_encoded_with_replies_indented_and_resolved_marked()
    {
        var comments = new[]
        {
            new ReportComment("ann", "2026-10-19 10:00", "Is this <b>right</b>?\n![shot](attach:a.png)", false, true, new Dictionary<string, EmbedData> { ["a.png"] = new EmbedData(true, "image/png", [1, 2, 3], null) }),
            new ReportComment("ben", "2026-10-19 11:00", "Yes **checked**", true, true),
        };
        var module = new ReportModule("M", ["Owner"], [new ReportRow("1", "SRS-1", "Text", 0, false, ["x"], "", "", false, null, comments), new ReportRow("2", "SRS-2", "No comments", 0, false, ["y"], "", "", false)]);
        var html = HtmlReport.Render("T", "now", [module]);
        Assert.Contains("<tr class=\"comments\"><td colspan=\"6\">", html);          // 3 fixed + 1 attribute + 2 link columns = 6... colspan = 5 + attributes
        Assert.Equal(1, html.Split("<tr class=\"comments\">").Length - 1);               // only the row that has comments
        Assert.Contains("Is this &lt;b&gt;right&lt;/b&gt;?", html);
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.Contains("class=\"c reply\"", html);
        Assert.Contains("<strong>checked</strong>", html);
        Assert.Contains("resolved", html);
        Assert.DoesNotContain("<b>", html);
    }
}
