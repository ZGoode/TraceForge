namespace TraceForge.Domain;

/// <summary>One object of a section being pasted. Items are listed parents first.
/// ParentObjectId null means "attach to the paste target's parent"; otherwise it names an earlier item.</summary>
public sealed record SectionItem(EngineeringObject Object, ObjectId? ParentObjectId, bool IsHeading, IReadOnlyDictionary<string, string> Values);

/// <summary>A deleted section (an object and everything below it) that can be restored as a unit.</summary>
public sealed record DeletedSection(ObjectId BatchId, ObjectId RootId, string CanonicalId, string Title, int Count, DateTimeOffset DeletedUtc);

/// <summary>Order that sorts embedded numbers numerically ("R-2" before "R-10", "1.2" before "1.10"), ignoring case.</summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; var sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
