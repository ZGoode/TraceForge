using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class EmbedMarkupTests
{
    [Fact] public void Pipe_tables_become_table_blocks_with_a_header_when_a_separator_row_follows()
    {
        var blocks = Markup.ParseBlocks("Intro\n| Mode | Limit |\n|---|:--:|\n| Run | **21** |\nOutro");
        Assert.Equal(3, blocks.Count);
        var table = Assert.IsType<TableBlock>(blocks[1]);
        Assert.True(table.HasHeader);
        Assert.Equal(2, table.Rows.Count);                       // separator row is not data
        Assert.Equal(2, table.Rows[1].Count);
        Assert.True(table.Rows[1][1][0].Bold);
        Assert.Equal("Intro \u23CE [table 2\u00D72] \u23CE Outro", Markup.Plain("Intro\n| Mode | Limit |\n|---|:--:|\n| Run | **21** |\nOutro"));
        Assert.False(Assert.IsType<TableBlock>(Markup.ParseBlocks("| a | b |\n| c | d |")[0]).HasHeader);
    }

    [Fact] public void Embed_lines_are_found_by_attachment_name_and_never_swallow_other_text()
    {
        var text = "See below\n![Wiring diagram](attach:wiring.PNG)\n![](attach:limits.csv)\n![x](http://evil.example/a.png)";
        Assert.Equal(new[] { "wiring.PNG", "limits.csv" }, Markup.EmbedNames(text).ToArray());
        var blocks = Markup.ParseBlocks(text);
        Assert.Equal("Wiring diagram", Assert.IsType<EmbedBlock>(blocks[1]).Alt);
        Assert.IsType<ParagraphBlock>(blocks[3]);                // a normal web link is just text, nothing is ever fetched
        Assert.Contains("[image: Wiring diagram]", Markup.Plain(text));
        Assert.Contains("[limits.csv]", Markup.Plain(text));
    }

    [Fact] public void Html_embeds_pictures_as_data_uris_and_tables_as_encoded_tables()
    {
        var embeds = new Dictionary<string, EmbedData>(StringComparer.OrdinalIgnoreCase)
        {
            ["pic.png"] = new EmbedData(true, "image/png", [1, 2, 3], null),
            ["t.csv"] = new EmbedData(false, "text/csv", null, new[] { new[] { "A", "B" }, new[] { "<script>x</script>", "2" } }),
        };
        var html = Markup.ToHtml("![A <cap>](attach:pic.png)\n![Data](attach:t.csv)\n![](attach:gone.png)", embeds);
        Assert.Contains("src=\"data:image/png;base64,AQID\"", html);
        Assert.Contains("alt=\"A &lt;cap&gt;\"", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attachment not found: gone.png", html);
        Assert.DoesNotContain("<img", Markup.ToHtml("![a](attach:pic.png)"));       // without the data the page never shows a picture
    }

    [Fact] public void Only_safe_picture_types_are_embedded()
    {
        Assert.Equal("image/png", Markup.ImageMime("a.PNG"));
        Assert.Null(Markup.ImageMime("a.svg"));
        Assert.Null(Markup.ImageMime("a.html"));
        Assert.True(Markup.IsTableFile("x.TSV"));
        Assert.False(Markup.IsTableFile("x.xlsx"));
    }
}
