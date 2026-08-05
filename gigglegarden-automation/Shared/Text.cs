using System.Globalization;
using System.Text;

namespace GiggleGarden.Shared;

public static class Text
{
    // Filename-safe slug. Operates on the FILTERED string throughout — slicing a
    // filtered string by the ORIGINAL length is an ArgumentOutOfRangeException
    // waiting for the first title that contains "|" or "!".
    public static string Slug(string value, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(value)) return "video";

        var sb = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            // Keep ASCII alphanumerics only. Devanagari/Gurmukhi titles are letters
            // and would survive char.IsLetterOrDigit, but they make brittle Windows
            // filenames and unusable URLs, so they collapse to separators here and
            // the language suffix carries the distinction.
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug.Length == 0 ? "video" : slug;
    }

    // Truncate to a platform caption limit without splitting a surrogate pair or a
    // word. Counts UTF-16 code units, which is what every platform limit is measured in.
    public static string Fit(string value, int maxLength, string ellipsis = "…")
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
        if (maxLength <= ellipsis.Length) return value[..maxLength];

        var cut = maxLength - ellipsis.Length;
        if (char.IsLowSurrogate(value[cut])) cut--;

        var lastSpace = value.LastIndexOf(' ', Math.Max(0, cut - 1));
        if (lastSpace > cut * 2 / 3) cut = lastSpace;

        return value[..cut].TrimEnd() + ellipsis;
    }

    // Word-wraps narration for the FFmpeg drawtext filter, which does no wrapping of
    // its own — a long sentence otherwise runs off both edges of the frame.
    public static IReadOnlyList<string> WrapLines(string value, int maxCharsPerLine, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return lines;

        var current = new StringBuilder();
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxCharsPerLine)
            {
                lines.Add(current.ToString());
                current.Clear();
                if (lines.Count == maxLines) return lines;
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }

        if (current.Length > 0 && lines.Count < maxLines) lines.Add(current.ToString());
        return lines;
    }

    // Roughly proportional to rendered width: Devanagari and Gurmukhi glyphs are wider
    // than Latin at the same point size, so they need a shorter line budget.
    public static int CharsPerLineFor(string language, int frameWidth, int fontSize)
    {
        var perGlyph = language switch { "hi" or "pa" => 0.62, _ => 0.52 };
        var usable = frameWidth * 0.88;
        return Math.Max(12, (int)(usable / (fontSize * perGlyph)));
    }

    public static string LanguageName(string code) => code switch
    {
        "hi" => "Hindi (Devanagari script)",
        "pa" => "Punjabi (Gurmukhi script)",
        _ => "English",
    };

    // BCP-47 tag for platform metadata. Sending "en" on a Hindi video mislabels it
    // for every recommendation and accessibility system downstream.
    public static string LanguageTag(string code) => code switch
    {
        "hi" => "hi",
        "pa" => "pa",
        _ => "en",
    };

    public static string Tail(string value, int n) =>
        string.IsNullOrEmpty(value) || value.Length <= n ? value ?? "" : value[^n..];

    public static string Bytes(long count) => count switch
    {
        >= 1L << 30 => $"{count / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{count / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{count / (double)(1L << 10):0.##} KB",
        _ => $"{count} B",
    };

    public static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
