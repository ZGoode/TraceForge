using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class NaturalOrderTests
{
    private static string[] Sorted(params string[] values) => values.OrderBy(v => v, NaturalOrder.Instance).ToArray();

    [Fact] public void Embedded_numbers_sort_numerically() =>
        Assert.Equal(new[] { "SRS-2", "SRS-10", "SRS-100" }, Sorted("SRS-100", "SRS-2", "SRS-10"));

    [Fact] public void Outline_numbers_sort_by_part() =>
        Assert.Equal(new[] { "1", "1.2", "1.10", "2", "10" }, Sorted("10", "1.10", "2", "1", "1.2"));

    [Fact] public void Case_is_ignored_and_leading_zeros_do_not_matter() =>
        Assert.Equal(0, NaturalOrder.Instance.Compare("req-007", "REQ-7"));

    [Fact] public void Prefix_sorts_before_longer_text_and_null_is_empty()
    {
        Assert.True(NaturalOrder.Instance.Compare("A", "AB") < 0);
        Assert.Equal(0, NaturalOrder.Instance.Compare(null, ""));
    }
}
