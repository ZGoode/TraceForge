using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class HtmlReportTests
{
    [Fact] public void Encodes_every_piece_of_text_and_contains_no_script()
    {
        var module = new ReportModule("A <b>&</b>", ["Owner"], [new ReportRow("1", "SRS-1", "<script>alert(1)</script>", 0, false, ["x\"y"], "SYS-1", "", true)]);
        var html = HtmlReport.Render("Title & more", "now", [module]);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("A &lt;b&gt;&amp;&lt;/b&gt;", html);
        Assert.Contains("Title &amp; more", html);
        Assert.Contains("(suspect)", html);
        Assert.StartsWith("<!DOCTYPE html>", html);
    }

    [Fact] public void Adds_a_contents_list_only_for_several_modules()
    {
        var one = new ReportModule("One", [], []); var two = new ReportModule("Two", [], []);
        Assert.DoesNotContain("<nav>", HtmlReport.Render("T", "g", [one]));
        Assert.Contains("<nav>", HtmlReport.Render("T", "g", [one, two]));
    }
}
