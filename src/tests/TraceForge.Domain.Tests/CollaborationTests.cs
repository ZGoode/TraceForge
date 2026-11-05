using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class CollaborationTests
{
    [Theory]
    [InlineData("tool.EXE", true)] [InlineData("run.ps1", true)] [InlineData("shortcut.lnk", true)]
    [InlineData("spec.pdf", false)] [InlineData("diagram.png", false)] [InlineData("noextension", false)]
    public void Risky_file_types_are_recognised_case_insensitively(string name, bool risky) => Assert.Equal(risky, ObjectAttachment.IsRisky(name));

    [Theory]
    [InlineData(@"C:\temp\spec.pdf", "spec.pdf")] [InlineData("../../etc/passwd", "passwd")] [InlineData("  a:b.txt ", "ab.txt")]
    public void Clean_name_keeps_only_a_safe_file_name(string input, string expected) => Assert.Equal(expected, ObjectAttachment.CleanName(input));

    [Theory] [InlineData("")] [InlineData("   ")] [InlineData("..")] [InlineData(null)]
    public void Clean_name_rejects_names_with_nothing_left(string? input) => Assert.Throws<ArgumentException>(() => ObjectAttachment.CleanName(input));
}
