namespace EasyDocs.Api.Versioning;

/// <summary>
/// Pure version-numbering rules R1–R8. The counter (Major, Minor, Rev) is the
/// single source of truth (spec §5.1); these functions never touch DB or I/O.
/// </summary>
public static class Numbering
{
    // R1/R2/R7: a draft only bumps the revision.
    public static (int Major, int Minor, int Rev) NextDraft((int Major, int Minor, int Rev) c)
        => (c.Major, c.Minor, c.Rev + 1);

    // R3: publishing a minor bumps Minor, resets Rev.
    public static (int Major, int Minor, int Rev) PublishMinor((int Major, int Minor, int Rev) c)
        => (c.Major, c.Minor + 1, 0);

    // R4: publishing a major bumps Major, resets Minor and Rev.
    public static (int Major, int Minor, int Rev) PublishMajor((int Major, int Minor, int Rev) c)
        => (c.Major + 1, 0, 0);

    // R5: manual override allows any non-negative counter.
    public static (int Major, int Minor, int Rev) Manual(int major, int minor, int rev)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor));
        if (rev < 0) throw new ArgumentOutOfRangeException(nameof(rev));
        return (major, minor, rev);
    }

    // R8: download filename "{orgSlug}__{sanitizedDocName}-v{M}.{m}.{r}.{ext}".
    public static string DownloadFileName(string orgSlug, string docName, (int Major, int Minor, int Rev) c, string ext)
    {
        var sanitized = Sanitize(docName);
        return $"{orgSlug}__{sanitized}-v{c.Major}.{c.Minor}.{c.Rev}.{ext}";
    }

    private static string Sanitize(string docName)
    {
        var sb = new System.Text.StringBuilder(docName.Length);
        foreach (var ch in docName.Trim())
        {
            if (ch == ' ') sb.Append('_');
            else if (ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(ch)) { }
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
