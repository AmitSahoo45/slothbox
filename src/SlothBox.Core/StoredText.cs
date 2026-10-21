using System.Text;

namespace SlothBox.Core;

// Text bound for the database: Postgres text cannot hold NUL, and Npgsql refuses half a surrogate pair.
public static class StoredText
{
    // Drops control characters (keeping tab, CR and LF) and any surrogate without its partner.
    public static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                clean.Append(c).Append(text[++i]);
            else if (!char.IsSurrogate(c) && (!char.IsControl(c) || c is '\t' or '\r' or '\n'))
                clean.Append(c);
        }
        return clean.ToString();
    }

    // At most max characters, never ending in the first half of a surrogate pair.
    public static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..(max > 0 && char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
}
