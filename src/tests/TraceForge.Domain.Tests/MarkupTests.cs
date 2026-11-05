using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class MarkupTests
{
    [Fact] public void Bold_italic_and_bullets_are_parsed_per_line()
    {
        var lines = Markup.Parse("The **system** shall *always* log\n- first\n* second");
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines[0].Runs, r => r.Text == "system" && r.Bold && !r.Italic);
        Assert.Contains(lines[0].Runs, r => r.Text == "always" && r.Italic && !r.Bold);
        Assert.True(lines[1].IsBullet); Assert.True(lines[2].IsBullet);
        Assert.Equal("first", Assert.Single(lines[1].Runs).Text);
    }

    [Theory]
    [InlineData("2 * 3 = 6", "2 * 3 = 6")]                 // a lone marker is literal
    [InlineData("**open only", "**open only")]
    [InlineData(@"price \*net\*", "price *net*")]          // escaped markers are literal
    [InlineData("**a** and *b*", "a and b")]
    [InlineData("one\r\ntwo", "one \u23CE two")]
    public void Plain_text_removes_only_real_markers(string input, string expected) => Assert.Equal(expected, Markup.Plain(input));

    [Fact] public void Html_encodes_all_text_before_adding_its_own_tags()
    {
        var html = Markup.ToHtml("**<img src=x onerror=alert(1)>** & *i*\n- a<b");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("<strong>&lt;img src=x onerror=alert(1)&gt;</strong> &amp; <em>i</em>", html);
        Assert.Contains("<ul><li>a&lt;b</li></ul>", html);
    }

    [Fact] public void Empty_or_null_text_gives_no_html() { Assert.Equal("", Markup.ToHtml(null)); Assert.Equal("", Markup.Plain("")); }
}
