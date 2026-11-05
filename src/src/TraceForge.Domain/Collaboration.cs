namespace TraceForge.Domain;

/// <summary>A review comment on an object. Threads are one level deep: a reply has ParentId set to the root comment.
/// Comments are never edited or deleted; a thread is resolved or reopened by appending an event. Resolved is meaningful on roots.</summary>
public sealed record Comment(ObjectId Id, ObjectId ObjectId, ObjectId? ParentId, string Author, string Body, DateTimeOffset CreatedUtc, bool Resolved)
{
    public const int MaxLength = 4000;

    /// <summary>Trims the text and rejects empty or over-long comments.</summary>
    public static string Validate(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("A comment cannot be empty.", nameof(body));
        if (text.Length > MaxLength) throw new ArgumentException($"A comment is limited to {MaxLength} characters.", nameof(body));
        return text;
    }
}

/// <summary>A file attached to an object. The file itself lives in the project's attachments folder (AttachmentId names it);
/// removing an attachment hides the record and keeps the file. CommentId is set when the file was attached to a comment of the object.</summary>
public sealed record ObjectAttachment(ObjectId Id, ObjectId ObjectId, string AttachmentId, string FileName, long SizeBytes, string Sha256, DateTimeOffset AddedUtc, string Actor, ObjectId? CommentId = null)
{
    public const long MaxBytes = 50L * 1024 * 1024;

    static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".msp", ".scr", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".jar", ".dll", ".cpl", ".pif" };

    /// <summary>True for file types that run code when opened. TraceForge never launches these; the user can save a copy instead.</summary>
    public static bool IsRisky(string fileName) => RiskyExtensions.Contains(System.IO.Path.GetExtension(fileName));

    /// <summary>Keeps only the file name part and removes characters that are invalid in names, so a record can never point outside the package.</summary>
    public static string CleanName(string? fileName)
    {
        var name = System.IO.Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) throw new ArgumentException("An attachment needs a file name.", nameof(fileName));
        return name.Length > 200 ? name[..200] : name;
    }
}
